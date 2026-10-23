#include "pch.h"
#include "PipeFrameClient.h"

namespace
{
	// Reconnect ladder: fast while the app is merely restarting, capped so a
	// permanently absent app costs one wakeup per second.
	constexpr DWORD kMinBackoffMs = 50;
	constexpr DWORD kMaxBackoffMs = 1000;

	// Frames are 1280x720 / 1920x1080 NV12 in practice; refuse anything absurd so
	// a desynchronised stream cannot trigger a huge allocation.
	constexpr DWORD kMaxPayloadBytes = 64u * 1024 * 1024;
}

HRESULT PipeFrameClient::Start()
{
	std::lock_guard<std::mutex> lifecycle(_lifecycleLock);
	if (_running)
		return S_OK;

	if (!_stopEvent)
		_stopEvent.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
	if (!_readEvent)
		_readEvent.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
	RETURN_LAST_ERROR_IF_NULL(_stopEvent.get());
	RETURN_LAST_ERROR_IF_NULL(_readEvent.get());

	ResetEvent(_stopEvent.get());
	ResetEvent(_readEvent.get());

	// A fresh activation starts from "no frame yet": the previous session's frame
	// must not be served to a new consumer, and the stream waits for the first
	// frame of this session (see MediaStream::RequestSample).
	{
		std::lock_guard<std::mutex> lock(_frameLock);
		_frame.clear();
		_frameInfo = WsFrameInfo{};
	}
	_hasFrame = false;

	_running = true;
	try
	{
		_thread = std::thread([this]() { Run(); });
	}
	catch (...)
	{
		// Thread creation failure (handle/stack exhaustion) must not fail the
		// activation: the stream serves placeholders and a later Start retries.
		_running = false;
		WINTRACE(L"PipeFrameClient: could not create the reader thread");
		return S_OK;
	}
	return S_OK;
}

void PipeFrameClient::Stop()
{
	std::lock_guard<std::mutex> lifecycle(_lifecycleLock);
	if (!_running.exchange(false))
		return;

	// Wake the reader instead of joining it while it is parked: it may be inside
	// WaitNamedPipe (bounded), the reconnect backoff, or an overlapped pipe read.
	// The pending read is cancelled by the reader itself (see ReadExact) because
	// CancelIoEx must run on the thread that issued it.
	if (_stopEvent)
		SetEvent(_stopEvent.get());

	if (_thread.joinable())
		_thread.join();

	if (_stopEvent)
		ResetEvent(_stopEvent.get());
}

PipeFrameClient::~PipeFrameClient()
{
	Stop();
}

bool PipeFrameClient::CopyLatestFrame(std::vector<BYTE>& out, WsFrameInfo& info)
{
	std::lock_guard<std::mutex> lock(_frameLock);
	if (!_hasFrame || _frame.empty())
		return false;

	out = _frame;
	info = _frameInfo;
	return true;
}

bool PipeFrameClient::CopyLatestFrameInto(BYTE* dst, DWORD dstCapacity, WsFrameInfo& info, DWORD* copied)
{
	if (copied)
		*copied = 0;
	if (!dst || !dstCapacity)
		return false;

	// Same single copy as CopyLatestFrame, but into the destination buffer: the
	// media stream hands in the memory of an outgoing pooled sample, so a frame
	// no longer travels pipe -> _frame -> vector -> MF buffer.
	std::lock_guard<std::mutex> lock(_frameLock);
	if (!_hasFrame || _frame.empty())
		return false;

	const size_t bytes = std::min<size_t>(_frame.size(), dstCapacity);
	memcpy(dst, _frame.data(), bytes);
	info = _frameInfo;
	if (copied)
		*copied = (DWORD)bytes;
	return bytes != 0;
}

bool PipeFrameClient::ReadExact(HANDLE pipe, void* buf, DWORD count)
{
	BYTE* p = static_cast<BYTE*>(buf);
	DWORD total = 0;
	while (total < count)
	{
		if (!_running)
			return false;

		// Overlapped read with a completion event of its own, so a read that
		// cannot complete (no frames while the app is idle) does not pin this
		// thread inside ReadFile where Stop() could only wait for it.
		OVERLAPPED ov{};
		ov.hEvent = _readEvent.get();
		ResetEvent(ov.hEvent);

		DWORD got = 0;
		if (!ReadFile(pipe, p + total, count - total, &got, &ov))
		{
			if (GetLastError() != ERROR_IO_PENDING)
				return false;

			const HANDLE waits[] = { ov.hEvent, _stopEvent.get() };
			if (WaitForMultipleObjects(ARRAYSIZE(waits), waits, FALSE, INFINITE) != WAIT_OBJECT_0)
			{
				// Stop() requested: cancel the pending read and drain its
				// completion before the pipe handle is closed below.
				CancelIoEx(pipe, &ov);
				WaitForSingleObject(ov.hEvent, INFINITE);
				return false;
			}

			if (!GetOverlappedResult(pipe, &ov, &got, FALSE) || got == 0)
				return false;
		}
		else if (got == 0)
		{
			return false;
		}

		total += got;
	}
	return true;
}

bool PipeFrameClient::Backoff(DWORD milliseconds)
{
	// Same reconnect backoff as before, but Stop() does not have to wait it out.
	if (!_stopEvent)
	{
		Sleep(milliseconds);
		return !_running;
	}

	return WaitForSingleObject(_stopEvent.get(), milliseconds) == WAIT_OBJECT_0;
}

void PipeFrameClient::Run()
{
	// A reader thread that ends leaves the stream without frames for the life of
	// the process (the Frame Server never restarts us, so a fresh activation gets
	// an empty stream). Re-enter the connection loop for as long as the client is
	// running, whatever happened inside it - allocation failure included.
	while (_running)
	{
		try
		{
			RunOnce();
		}
		catch (...)
		{
			WINTRACE(L"PipeFrameClient: reader recovered from an exception; retrying");
		}

		if (!_running)
			break;
		if (Backoff(kMaxBackoffMs))
			break;
	}
}

void PipeFrameClient::RunOnce()
{
	{
		DWORD backoffMs = kMinBackoffMs;
		while (_running)
		{
			if (!WaitNamedPipeW(_pipeName.c_str(), 3000))
			{
				if (Backoff(backoffMs))
					break;
				backoffMs = std::min<DWORD>(backoffMs * 2, kMaxBackoffMs);
				continue;
			}

			// FILE_FLAG_OVERLAPPED: reads must be cancellable, see ReadExact.
			HANDLE pipe = CreateFileW(_pipeName.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, nullptr);
			if (pipe == INVALID_HANDLE_VALUE)
			{
				if (Backoff(backoffMs))
					break;
				backoffMs = std::min<DWORD>(backoffMs * 2, kMaxBackoffMs);
				continue;
			}

			DWORD mode = PIPE_READMODE_BYTE;
			SetNamedPipeHandleState(pipe, &mode, nullptr, nullptr);

			// First message must be a Config message.
			WsMsgHeader hdr{};
			if (!ReadExact(pipe, &hdr, sizeof(hdr)) ||
				hdr.magic != WS_MSG_MAGIC ||
				hdr.type != WsMsgType_Config ||
				hdr.payloadLen != sizeof(WsConfigMsg))
			{
				CloseHandle(pipe);
				if (Backoff(backoffMs))
					break;
				backoffMs = std::min<DWORD>(backoffMs * 2, kMaxBackoffMs);
				continue;
			}

			WsConfigMsg cfg{};
			if (!ReadExact(pipe, &cfg, sizeof(cfg)))
			{
				CloseHandle(pipe);
				if (Backoff(backoffMs))
					break;
				continue;
			}

			_width = cfg.width;
			_height = cfg.height;
			_fpsNum = cfg.fpsNum;
			_fpsDen = cfg.fpsDen;
			backoffMs = kMinBackoffMs; // connected: next drop starts the ladder over
			_connectCount++;
			WINTRACE(L"PipeFrameClient config %ux%u format=%u fps=%u/%u", cfg.width, cfg.height, cfg.format, cfg.fpsNum, cfg.fpsDen);

			// Frame loop.
			while (_running)
			{
				if (!ReadExact(pipe, &hdr, sizeof(hdr)))
					break; // disconnected; reconnect below

				// A corrupt or desynchronised stream must not make us allocate a
				// bogus payload (that used to be able to throw or exhaust memory).
				if (hdr.magic != WS_MSG_MAGIC || hdr.payloadLen > kMaxPayloadBytes)
					break;

				if (hdr.type == WsMsgType_Frame && hdr.payloadLen >= sizeof(WsFrameInfo))
				{
					std::vector<BYTE> payload(hdr.payloadLen);
					if (!ReadExact(pipe, payload.data(), hdr.payloadLen))
						break;

					const WsFrameInfo* fi = reinterpret_cast<const WsFrameInfo*>(payload.data());
					std::lock_guard<std::mutex> lock(_frameLock);
					_frame.assign(payload.begin() + sizeof(WsFrameInfo), payload.end());
					_frameInfo = *fi;
					_hasFrame = true;
					_framesReceived++;
				}
				else if (hdr.payloadLen > 0)
				{
					// Unknown message type; skip its payload.
					std::vector<BYTE> skip(hdr.payloadLen);
					if (!ReadExact(pipe, skip.data(), hdr.payloadLen))
						break;
				}
			}

			CloseHandle(pipe);
			if (Backoff(backoffMs))
				break;
			backoffMs = std::min<DWORD>(backoffMs * 2, kMaxBackoffMs);
		}
	}
}
