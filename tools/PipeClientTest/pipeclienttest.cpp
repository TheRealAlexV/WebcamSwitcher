// pipeclienttest.cpp - diagnostic harness for PipeFrameClient (not shipped).
//
// Proves, without any camera or Frame Server, the client-side half of the
// "slot goes permanently dead" failure:
//   1. a normal server connection delivers frames
//   2. the server process/handle dying is survived (reader keeps running)
//   3. a server reappearing on the same pipe name is reconnected and frames flow
//   4. a client started BEFORE the server exists still connects later (no latch)
//   5. Stop() releases a reader that is parked waiting for a server
//
// Build: tools\PipeClientTest\run.cmd   (Release, x64)

#include "pch.h"
#include <cstdio>
#include "PipeFrameClient.h"

namespace
{
	constexpr wchar_t kPipeName[] = L"\\\\.\\pipe\\WebcamSwitcher.PipeClientTest";
	constexpr UINT32 kWidth = 1280;
	constexpr UINT32 kHeight = 720;
	constexpr DWORD kNv12Size = kWidth * kHeight * 3 / 2;

	int g_failures = 0;

	void Check(bool ok, const char* what, const char* detail = nullptr)
	{
		std::printf("  [%s] %s%s%s\n", ok ? "PASS" : "FAIL", what,
			detail ? " - " : "", detail ? detail : "");
		if (!ok)
			g_failures++;
	}

	HANDLE CreateInstance()
	{
		return CreateNamedPipeW(kPipeName,
			PIPE_ACCESS_DUPLEX,
			PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
			PIPE_UNLIMITED_INSTANCES,
			1 << 20, 1 << 20, 0, nullptr);
	}

	bool WriteAll(HANDLE pipe, const void* data, DWORD length)
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

	// Minimal server: accepts one client, sends config, then frames every `periodMs`
	// until `stop` is signalled. Returns the handle it used, or nullptr.
	struct Server
	{
		HANDLE pipe = INVALID_HANDLE_VALUE;
		HANDLE thread = nullptr;
		HANDLE stop = nullptr;
		std::atomic<UINT64> frames{ 0 };
		char error[128]{};

		bool Start(const wchar_t* what)
		{
			stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
			pipe = CreateInstance();
			if (pipe == INVALID_HANDLE_VALUE)
			{
				std::snprintf(error, sizeof(error), "%ws: CreateNamedPipe %lu", what, GetLastError());
				return false;
			}
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
			if (!WriteAll(pipe, &configHeader, sizeof(configHeader)) || !WriteAll(pipe, &config, sizeof(config)))
				return 2;

			std::vector<BYTE> payload(sizeof(WsFrameInfo) + kNv12Size);
			auto* info = reinterpret_cast<WsFrameInfo*>(payload.data());
			BYTE* pixels = payload.data() + sizeof(WsFrameInfo);

			for (;;)
			{
				if (WaitForSingleObject(stop, 5) == WAIT_OBJECT_0)
					return 0;
				frames++;
				*info = WsFrameInfo{};
				info->width = kWidth;
				info->height = kHeight;
				info->stride = kWidth;
				info->format = WsPixelFormat_NV12;
				info->sequence = frames.load();
				std::memset(pixels, 0x10, kWidth * kHeight);
				std::memset(pixels + kWidth * kHeight, 0x80, kNv12Size - kWidth * kHeight);

				WsMsgHeader header{ WS_MSG_MAGIC, WS_PROTOCOL_VERSION, WsMsgType_Frame, (UINT32)payload.size() };
				if (!WriteAll(pipe, &header, sizeof(header)) || !WriteAll(pipe, payload.data(), (DWORD)payload.size()))
					return 3; // client died: stop writing, as the real app would drop it
			}
		}

		void Kill()
		{
			if (stop)
				SetEvent(stop);
			if (thread)
			{
				// The thread may be parked in ConnectNamedPipe (no client) or in a
				// write: cancel either, then wait for it to unwind before the
				// handle goes away. DisconnectNamedPipe is deliberately not used -
				// it can block on a half-closed connection.
				CancelSynchronousIo(thread);
				WaitForSingleObject(thread, 2000);
				CloseHandle(thread);
				thread = nullptr;
			}
			if (pipe != INVALID_HANDLE_VALUE)
			{
				CloseHandle(pipe);
				pipe = INVALID_HANDLE_VALUE;
			}
			if (stop)
			{
				CloseHandle(stop);
				stop = nullptr;
			}
		}
	};

	bool WaitForFrames(PipeFrameClient& client, UINT64 target, DWORD timeoutMs)
	{
		const DWORD deadline = GetTickCount() + timeoutMs;
		while (GetTickCount() < deadline)
		{
			if (client.FramesReceived() >= target)
				return true;
			Sleep(5);
		}
		return client.FramesReceived() >= target;
	}
}

int main()
{
	setvbuf(stdout, nullptr, _IONBF, 0);
	std::printf("PipeFrameClient diagnostic\n");
	{
		std::printf("\n1) normal connection\n");
		PipeFrameClient client;
		client.SetPipeName(kPipeName); // never the live app's pipe
		Server server;
		Check(server.Start(L"1"), "server instance created", server.error);
		Check(SUCCEEDED(client.Start()), "client Start()");
		Check(WaitForFrames(client, 5, 3000), "received 5+ frames");
		Check(client.HasFrame(), "HasFrame() after frames");

		std::vector<BYTE> frame;
		WsFrameInfo info{};
		DWORD copied = 0;
		Check(client.CopyLatestFrameInto(frame.size() ? frame.data() : nullptr, 0, info, &copied) == false,
			"CopyLatestFrameInto rejects a null destination");
		frame.resize(kNv12Size);
		Check(client.CopyLatestFrameInto(frame.data(), (DWORD)frame.size(), info, &copied) && copied == kNv12Size,
			"one copy of a full 1280x720 NV12 frame");
		Check(info.width == kWidth && info.height == kHeight && info.stride == kWidth,
			"frame metadata round-trips");
		std::printf("  frames received=%llu connects=%llu\n",
			(unsigned long long)client.FramesReceived(), (unsigned long long)client.ConnectCount());

		std::printf("\n2) server dies mid-stream (client process killed without Shutdown)\n");
		const UINT64 beforeKill = client.FramesReceived();
		server.Kill();
		Sleep(300);
		std::printf("  [dbg] after kill: client frames %llu -> %llu (server wrote %llu)\n",
			(unsigned long long)beforeKill, (unsigned long long)client.FramesReceived(),
			(unsigned long long)server.frames.load());
		Sleep(500);
		std::printf("  [dbg] 500 ms later: client frames %llu, hasFrame=%d\n",
			(unsigned long long)client.FramesReceived(), (int)client.HasFrame());
		Check(client.IsRunning(), "reader thread still running after the server died");
		Check(client.FramesReceived() >= beforeKill, "frame counter did not go backwards");

		std::printf("\n3) server reappears on the same pipe name\n");
		Server server2;
		const UINT64 beforeRestart = client.FramesReceived();
		Check(server2.Start(L"3"), "second server instance created", server2.error);
		const DWORD t0 = GetTickCount();
		const bool reconnected = WaitForFrames(client, beforeRestart + 3, 4000);
		const DWORD reconnectMs = GetTickCount() - t0;
		Check(reconnected, "frames flow again after the server was recreated");
		std::printf("  reconnect + 3 frames in %lu ms, connects=%llu\n",
			reconnectMs, (unsigned long long)client.ConnectCount());
		Check(client.ConnectCount() >= 2, "connected at least twice (reconnect happened)");
		server2.Kill();

		std::printf("\n4) Stop() with no server present\n");
		const DWORD stopStart = GetTickCount();
		client.Stop();
		const DWORD stopMs = GetTickCount() - stopStart;
		Check(!client.IsRunning(), "client stopped");
		Check(stopMs < 200, "Stop() did not wait out a reconnect delay");
		std::printf("  Stop() took %lu ms\n", stopMs);
	}

	std::printf("\n5) client started BEFORE any server exists\n");
	{
		PipeFrameClient client;
		client.SetPipeName(kPipeName);
		Check(SUCCEEDED(client.Start()), "client Start() with no server");
		Sleep(1500);
		Check(client.IsRunning(), "still running after 1.5 s with no server");
		Check(!client.HasFrame(), "no frame yet, as expected");

		Server late;
		Check(late.Start(L"5"), "server created late", late.error);
		const DWORD t0 = GetTickCount();
		const bool got = WaitForFrames(client, 2, 4000);
		const DWORD ms = GetTickCount() - t0;
		Check(got, "late server is picked up");
		std::printf("  late-start connect + 2 frames in %lu ms, connects=%llu\n",
			ms, (unsigned long long)client.ConnectCount());

		std::printf("\n6) Stop() while connected, then a fresh Start() resyncs\n");
		client.Stop();
		Check(!client.IsRunning(), "stopped");
		late.Kill();

		Server third;
		Check(SUCCEEDED(client.Start()), "second Start() on the same client");
		const UINT64 base = client.FramesReceived();
		Check(third.Start(L"6"), "third server created", third.error);
		Check(WaitForFrames(client, base + 2, 4000), "frames flow after Stop()/Start()");
		client.Stop();
		third.Kill();
	}

	std::printf("\nRESULT: %s (%d failure(s))\n", g_failures ? "FAIL" : "PASS", g_failures);
	return g_failures ? 1 : 0;
}
