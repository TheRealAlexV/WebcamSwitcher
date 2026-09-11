#pragma once
#include <vector>
#include <string>
#include <mutex>
#include <thread>
#include <atomic>
#include <algorithm>
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

	// Copies the latest frame's pixel data into `out` and its metadata into
	// `info`. Returns false if no frame has been received yet.
	bool CopyLatestFrame(std::vector<BYTE>& out, WsFrameInfo& info);

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
	static bool ReadExact(HANDLE h, void* buf, DWORD count);

	std::wstring _pipeName{ WS_PIPE_NAME };
	std::thread _thread;
	std::atomic<bool> _running{ false };
	std::atomic<UINT32> _width{ 0 };
	std::atomic<UINT32> _height{ 0 };
	std::atomic<UINT32> _fpsNum{ 0 };
	std::atomic<UINT32> _fpsDen{ 0 };

	std::mutex _frameLock;
	std::vector<BYTE> _frame;
	WsFrameInfo _frameInfo{};
	std::atomic<bool> _hasFrame{ false };
};
