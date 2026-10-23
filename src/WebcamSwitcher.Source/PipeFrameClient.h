#pragma once
#include <vector>
#include <string>
#include <mutex>
#include <thread>
#include <atomic>
#include <algorithm>
#include <wil/resource.h>
#include "WebcamSwitcherProtocol.h"

// Connects to the frame pipe served by WebcamSwitcher.App and keeps the most
// recent frame available for the media stream to copy into outgoing samples.
// Runs a background thread that (re)connects as needed.
class PipeFrameClient
{
public:
	PipeFrameClient() = default;
	~PipeFrameClient();
	PipeFrameClient(const PipeFrameClient&) = delete;
	PipeFrameClient& operator=(const PipeFrameClient&) = delete;

	// Selects the pipe the reader thread connects to. Must be called before
	// Start(); an empty name restores the default switcher pipe.
	void SetPipeName(const std::wstring& name) { _pipeName = name.empty() ? WS_PIPE_NAME : name; }

	HRESULT Start();
	void Stop();
	bool IsRunning() const { return _running; }

	// Copies the latest frame's pixel data into `out` and its metadata into
	// `info`. Returns false if no frame has been received yet.
	bool CopyLatestFrame(std::vector<BYTE>& out, WsFrameInfo& info);

	// Copies the latest frame's pixel data straight into the caller's buffer (no
	// intermediate allocation) and its metadata into `info`. Returns false if no
	// frame has been received yet. `copied` (optional) receives the number of
	// bytes actually copied, which is 0 when the frame is empty.
	bool CopyLatestFrameInto(BYTE* dst, DWORD dstCapacity, WsFrameInfo& info, DWORD* copied = nullptr);

	// True once a frame has been received since the last Start(). A stream that
	// has never received one waits for the first frame instead of emitting an
	// endless run of placeholder samples.
	bool HasFrame() const { return _hasFrame; }

	// Diagnostic counters (atomic, cheap): frames received and successful
	// (re)connections since construction.
	UINT64 FramesReceived() const { return _framesReceived.load(); }
	UINT64 ConnectCount() const { return _connectCount.load(); }

	UINT32 Width() const { return _width ? _width.load() : WS_DEFAULT_WIDTH; }
	UINT32 Height() const { return _height ? _height.load() : WS_DEFAULT_HEIGHT; }

	// Sample duration in 100-ns units, derived from the negotiated frame rate.
	UINT64 FrameDuration100ns() const
	{
		UINT32 num = _fpsNum ? _fpsNum.load() : WS_DEFAULT_FPS_NUM;
		UINT32 den = _fpsDen ? _fpsDen.load() : WS_DEFAULT_FPS_DEN;
		if (!den)
			den = 1;
		return 10000000ULL * den / num;
	}

private:
	void Run();
	// One connection attempt (config + frame loop) until it drops or the client
	// stops. Run() wraps this so an exception can never end the reader thread.
	void RunOnce();
	bool ReadExact(HANDLE pipe, void* buf, DWORD count);
	// Reconnect backoff (and any other short wait) that Stop() can cut short.
	// Returns true when the client is stopping and the caller must unwind.
	bool Backoff(DWORD milliseconds);

	std::wstring _pipeName{ WS_PIPE_NAME };
	std::thread _thread;
	std::atomic<bool> _running{ false };
	std::mutex _lifecycleLock;       // serializes Start()/Stop()
	wil::unique_handle _stopEvent;   // manual reset, set by Stop(), waited by the reader
	wil::unique_handle _readEvent;   // manual reset, OVERLAPPED event for the pending read
	std::atomic<UINT32> _width{ 0 };
	std::atomic<UINT32> _height{ 0 };
	std::atomic<UINT32> _fpsNum{ 0 };
	std::atomic<UINT32> _fpsDen{ 0 };

	std::mutex _frameLock;
	std::vector<BYTE> _frame;
	WsFrameInfo _frameInfo{};
	std::atomic<bool> _hasFrame{ false };
	std::atomic<UINT64> _framesReceived{ 0 };
	std::atomic<UINT64> _connectCount{ 0 };
};
