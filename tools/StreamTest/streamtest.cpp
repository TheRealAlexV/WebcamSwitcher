// streamtest.cpp - diagnostic harness for MediaStream (not shipped).
//
// Drives the REAL MediaStream object (same translation unit as the DLL) without
// the Frame Server, and hammers the Stop/Start churn that an OBS graph rebuild
// produces, asserting that RequestSample never returns a failure HRESULT.
//
// Modes:
//   streamtest                    2000 cycles, no pipe client
//   streamtest --client           2000 cycles with a live PipeFrameClient attached
//   streamtest --starve           2000 cycles, publisher paused mid-way (placeholder path)
//   streamtest --cycles N         override the cycle count
//
// Build: tools\StreamTest\run.cmd

#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include <cstdio>
#include <atomic>
#include <thread>
#include "MFTools.h"
#include "MediaStream.h"
#include "PipeFrameClient.h"

// Only referenced by Tools.cpp's friendly-name helper; the harness has no COM server.
GUID CLSID_WebcamSwitcherSource = { 0x7c1b1a42, 0x5b6e, 0x4d8f, { 0x9a, 0x11, 0x3e, 0x2f, 0x00, 0xaa, 0xbb, 0x01 } };

namespace
{
	constexpr UINT32 kWidth = 1280;
	constexpr UINT32 kHeight = 720;
	constexpr DWORD kNv12Size = kWidth * kHeight * 3 / 2;
	constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\WebcamSwitcher.StreamTest";

	int g_failures = 0;
	void Check(bool ok, const char* what, const char* detail = nullptr)
	{
		std::printf("  [%s] %s%s%s\n", ok ? "PASS" : "FAIL", what, detail ? " - " : "", detail ? detail : "");
		if (!ok)
			g_failures++;
	}

	// Minimal IMFMediaSource: MediaStream only stores it.
	struct StubSource : winrt::implements<StubSource, IMFMediaSource>
	{
		STDMETHOD(GetCharacteristics)(DWORD* c) { *c = MFMEDIASOURCE_IS_LIVE; return S_OK; }
		STDMETHOD(CreatePresentationDescriptor)(IMFPresentationDescriptor** p) { *p = nullptr; return E_NOTIMPL; }
		STDMETHOD(Start)(IMFPresentationDescriptor*, const GUID*, const PROPVARIANT*) { return S_OK; }
		STDMETHOD(Stop)() { return S_OK; }
		STDMETHOD(Pause)() { return S_OK; }
		STDMETHOD(Shutdown)() { return S_OK; }
		STDMETHOD(BeginGetEvent)(IMFAsyncCallback*, IUnknown*) { return E_NOTIMPL; }
		STDMETHOD(EndGetEvent)(IMFAsyncResult*, IMFMediaEvent** e) { *e = nullptr; return E_NOTIMPL; }
		STDMETHOD(GetEvent)(DWORD, IMFMediaEvent** e) { *e = nullptr; return E_NOTIMPL; }
		STDMETHOD(QueueEvent)(MediaEventType, REFGUID, HRESULT, const PROPVARIANT*) { return S_OK; }
	};

	// Minimal publisher so the "--client" and "--starve" modes have a real pipe.
	struct Server
	{
		HANDLE pipe = INVALID_HANDLE_VALUE;
		HANDLE thread = nullptr;
		HANDLE stop = nullptr;
		std::atomic<UINT64> frames{ 0 };
		std::atomic<bool> paused{ false };

		bool Start()
		{
			stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
			pipe = CreateNamedPipeW(kPipeName, PIPE_ACCESS_DUPLEX,
				PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES,
				1 << 20, 1 << 20, 0, nullptr);
			if (pipe == INVALID_HANDLE_VALUE)
				return false;
			thread = CreateThread(nullptr, 0, &Server::Run, this, 0, nullptr);
			return thread != nullptr;
		}

		static DWORD WINAPI Run(void* self) { return static_cast<Server*>(self)->Loop(); }

		DWORD Loop()
		{
			if (!ConnectNamedPipe(pipe, nullptr) && GetLastError() != ERROR_PIPE_CONNECTED)
				return 1;

			WsMsgHeader configHeader{ WS_MSG_MAGIC, WS_PROTOCOL_VERSION, WsMsgType_Config, sizeof(WsConfigMsg) };
			WsConfigMsg config{ kWidth, kHeight, kWidth, WsPixelFormat_NV12, 30, 1 };
			if (!WriteAll(&configHeader, sizeof(configHeader)) || !WriteAll(&config, sizeof(config)))
				return 2;

			std::vector<BYTE> payload(sizeof(WsFrameInfo) + kNv12Size);
			auto* info = reinterpret_cast<WsFrameInfo*>(payload.data());
			BYTE* pixels = payload.data() + sizeof(WsFrameInfo);
			std::memset(pixels, 0x10, kWidth * kHeight);
			std::memset(pixels + kWidth * kHeight, 0x80, kNv12Size - kWidth * kHeight);

			for (;;)
			{
				if (WaitForSingleObject(stop, 5) == WAIT_OBJECT_0)
					return 0;
				if (paused.load())
					continue;

				frames++;
				*info = WsFrameInfo{};
				info->width = kWidth;
				info->height = kHeight;
				info->stride = kWidth;
				info->format = WsPixelFormat_NV12;
				info->sequence = frames.load();

				WsMsgHeader header{ WS_MSG_MAGIC, WS_PROTOCOL_VERSION, WsMsgType_Frame, (UINT32)payload.size() };
				if (!WriteAll(&header, sizeof(header)) || !WriteAll(payload.data(), (DWORD)payload.size()))
					return 3;
			}
		}

		bool WriteAll(const void* data, DWORD length)
		{
			const BYTE* p = static_cast<const BYTE*>(data);
			DWORD done = 0;
			while (done < length)
			{
				DWORD wrote = 0;
				if (!WriteFile(pipe, p + done, length - done, &wrote, nullptr) || wrote == 0)
					return false;
				done += wrote;
			}
			return true;
		}

		void Kill()
		{
			if (stop) SetEvent(stop);
			if (thread)
			{
				CancelSynchronousIo(thread);
				WaitForSingleObject(thread, 2000);
				CloseHandle(thread);
				thread = nullptr;
			}
			if (pipe != INVALID_HANDLE_VALUE) { CloseHandle(pipe); pipe = INVALID_HANDLE_VALUE; }
			if (stop) { CloseHandle(stop); stop = nullptr; }
		}
	};

	// Drains the stream's event queue and counts samples / bad payloads.
	struct Drain
	{
		int samples = 0;
		int wrongSize = 0;
		int badTime = 0;
		void operator()(MediaStream& stream)
		{
			for (;;)
			{
				wil::com_ptr_nothrow<IMFMediaEvent> ev;
				HRESULT hr = stream.GetEvent(MF_EVENT_FLAG_NO_WAIT, &ev);
				if (hr == MF_E_NO_EVENTS_AVAILABLE || !ev)
					return;
				if (FAILED(hr))
					return;
				MediaEventType type = MEUnknown;
				ev->GetType(&type);
				if (type != MEMediaSample)
					continue;
				// MEMediaSample carries the IMFSample as the event value.
				PROPVARIANT value;
				PropVariantInit(&value);
				wil::com_ptr_nothrow<IMFSample> sample;
				if (SUCCEEDED(ev->GetValue(&value)) && value.vt == VT_UNKNOWN && value.punkVal)
					value.punkVal->QueryInterface(IID_PPV_ARGS(&sample));
				PropVariantClear(&value);
				if (!sample)
					continue;
				samples++;
				DWORD len = 0;
				sample->GetTotalLength(&len);
				if (len != kNv12Size)
					wrongSize++;
				LONGLONG t = 0;
				if (FAILED(sample->GetSampleTime(&t)) || t < 0)
					badTime++;
			}
		}
	};
}

int main(int argc, char** argv)
{
	setvbuf(stdout, nullptr, _IONBF, 0);
	bool withClient = false, starve = false;
	int cycles = 2000;
	for (int i = 1; i < argc; i++)
	{
		if (!std::strcmp(argv[i], "--client")) withClient = true;
		else if (!std::strcmp(argv[i], "--starve")) { withClient = true; starve = true; }
		else if (!std::strcmp(argv[i], "--cycles") && i + 1 < argc) cycles = std::atoi(argv[++i]);
	}

	std::printf("MediaStream Stop/Start harness: %d cycles%s%s\n", cycles,
		withClient ? ", pipe client attached" : "", starve ? ", publisher paused mid-run" : "");

	RETURN_IF_FAILED(MFStartup(MF_VERSION));

	auto source = winrt::make_self<StubSource>();
	auto stream = winrt::make_self<MediaStream>();
	HRESULT initHr = stream->Initialize(source.get(), 0);
	Check(SUCCEEDED(initHr), "MediaStream::Initialize");
	if (FAILED(initHr))
		return 1;

	{
		// Report what the stream actually advertises: the harness measures against it.
		wil::com_ptr_nothrow<IMFStreamDescriptor> desc;
		stream->GetStreamDescriptor(&desc);
		wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
		wil::com_ptr_nothrow<IMFMediaType> type;
		if (desc) desc->GetMediaTypeHandler(&handler);
		if (handler) handler->GetCurrentMediaType(&type);
		UINT32 w = 0, h = 0, num = 0, den = 0;
		if (type)
		{
			MFGetAttributeSize(type.get(), MF_MT_FRAME_SIZE, &w, &h);
			MFGetAttributeRatio(type.get(), MF_MT_FRAME_RATE, &num, &den);
		}
		std::printf("  advertised: %ux%u @ %u/%u fps (harness expects %ux%u)\n", w, h, num, den, kWidth, kHeight);
	}

	PipeFrameClient client;
	Server server;
	if (withClient)
	{
		client.SetPipeName(kPipeName);
		Check(server.Start(), "test publisher started");
		Check(SUCCEEDED(client.Start()), "pipe client started");
		stream->SetFrameClient(&client);
		// Let a frame arrive before the churn starts.
		for (int i = 0; i < 100 && !client.HasFrame(); i++)
			Sleep(10);
		Check(client.HasFrame() || starve, "pipe frame received");
	}

	const bool singleThread = getenv("STREAMTEST_SINGLE") != nullptr;
	std::atomic<bool> running{ true };
	std::atomic<int> requests{ 0 };
	std::atomic<int> failed{ 0 };
	std::atomic<HRESULT> firstFailure{ S_OK };
	std::atomic<int> maxGapMs{ 0 };

	// Consumer: request samples the way the Frame Server does, and record any
	// failure HRESULT (one is enough to kill the virtual camera for good).
	std::thread consumer([&]()
	{
		if (singleThread)
			return;
		while (running)
		{
			unsigned long long t0 = GetTickCount64();
			HRESULT hr = stream->RequestSample(nullptr);
			int ms = (int)(GetTickCount64() - t0);
			if (ms > maxGapMs)
				maxGapMs = ms;

			requests++;
			if (FAILED(hr))
			{
				int n = ++failed;
				if (n == 1)
					firstFailure = hr;
			}
			Drain()(*stream); // keep the event queue from growing
		}
	});

	// Churn: Stop/Start at 1-20 ms intervals, exactly what an OBS graph rebuild does.
	LARGE_INTEGER freq{};
	QueryPerformanceFrequency(&freq);
	unsigned seed = 12345;
	int restartFailures = 0;
	int framesAfterRestart = 0;
	std::vector<int> perCycleGap;
	perCycleGap.reserve(cycles);

	for (int cycle = 0; cycle < cycles; cycle++)
	{
		if (starve && cycle == cycles / 2)
			server.paused = true;

		unsigned long long ts = GetTickCount64();
		HRESULT hrStop = stream->Stop();
		const int stopMs = (int)(GetTickCount64() - ts);
		if (FAILED(hrStop))
			restartFailures++;

		seed = seed * 1103515245 + 12345;
		const int delayMs = 1 + (int)((seed >> 16) % 20);
		Sleep(delayMs);

		unsigned long long ta = GetTickCount64();
		HRESULT hrStart = stream->Start(nullptr);
		const int startMs = (int)(GetTickCount64() - ta);
		if (FAILED(hrStart))
			restartFailures++;

		// The next request after Start must come back promptly and produce a
		// sample: measure it rather than assuming.
		unsigned long long t0 = GetTickCount64();
		HRESULT hrReq = stream->RequestSample(nullptr);
		const int gap = (int)(GetTickCount64() - t0);
		Drain()(*stream); // the Frame Server drains samples; returning them releases the pool
		if (getenv("STREAMTEST_TRACE") && (gap > 100 || startMs > 100 || stopMs > 100))
			std::printf("    cycle %d: stop=%d ms start=%d ms request=%d ms (delay=%d ms)\n",
				cycle, stopMs, startMs, gap, delayMs);
		perCycleGap.push_back(gap);
		if (FAILED(hrReq))
			failed++;
		if (gap <= 100) // one frame + generous scheduling margin
			framesAfterRestart++;
	}

	running = false;
	consumer.join();

	int worstGap = 0, p50 = 0;
	if (!perCycleGap.empty())
	{
		std::vector<int> sorted = perCycleGap;
		std::sort(sorted.begin(), sorted.end());
		worstGap = sorted.back();
		p50 = sorted[sorted.size() / 2];
	}

	std::printf("\n  cycles=%d  requests=%d  failed requests=%d  failed Stop/Start=%d\n",
		cycles, requests.load(), failed.load(), restartFailures);
	std::printf("  post-Start RequestSample: p50=%d ms worst=%d ms, responded within 100 ms in %d/%d cycles\n",
		p50, worstGap, framesAfterRestart, cycles);
	if (failed.load())
		std::printf("  first failure HRESULT = 0x%08X\n", (unsigned)firstFailure.load());
	if (withClient)
		std::printf("  pipe frames received=%llu\n", (unsigned long long)client.FramesReceived());

	Check(failed.load() == 0, "zero failed RequestSample calls");
	Check(restartFailures == 0, "zero failed Stop/Start calls");
	Check(framesAfterRestart == cycles, "every cycle resumed streaming within 100 ms");
	if (worstGap > 100)
		std::printf("  note: worst gap %d ms\n", worstGap);

	// Pacing check: with the publisher paused the stream emits placeholders; they
	// must still be spaced by the frame interval, never spun out in a burst.
	{
		if (withClient)
			server.paused = true;
		const int n = 12;
		unsigned long long t0 = GetTickCount64();
		int failedPaced = 0;
		for (int i = 0; i < n; i++)
			if (FAILED(stream->RequestSample(nullptr)))
				failedPaced++;
			else
				Drain()(*stream);
		const int total = (int)(GetTickCount64() - t0);
		const double perCall = (double)total / n;
		std::printf("  placeholder pacing (%s): %d requests in %d ms (%.1f ms each, frame interval 33.3 ms)\n",
			withClient ? "publisher paused" : "no pipe", n, total, perCall);
		Check(failedPaced == 0, "paced requests returned S_OK");
		// Must not run faster than the frame interval (allow scheduler jitter).
		Check(perCall >= 25.0, "placeholders are paced at the frame interval, not spun");
	}

	if (withClient)
	{
		// After the churn the stream must still deliver real frames. The starve
		// variant paused the publisher to exercise the placeholder path; let it
		// resume before checking that real frames come back.
		server.paused = false;
		Drain()(*stream);
		const UINT64 before = client.FramesReceived();
		for (int i = 0; i < 100 && client.FramesReceived() <= before; i++)
			Sleep(10);
		Check(client.FramesReceived() > before, "pipe frames still flowing after the churn");
		client.Stop();
		server.Kill();
	}

	stream->Shutdown();
	MFShutdown();

	std::printf("\nRESULT: %s (%d failure(s))\n", g_failures ? "FAIL" : "PASS", g_failures);
	return g_failures ? 1 : 0;
}
