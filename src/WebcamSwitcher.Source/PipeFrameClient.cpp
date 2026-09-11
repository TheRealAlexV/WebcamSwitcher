#include "pch.h"
#include "PipeFrameClient.h"

HRESULT PipeFrameClient::Start()
{
	if (_running)
		return S_OK;

	_running = true;
	_thread = std::thread([this]() { Run(); });
	return S_OK;
}

void PipeFrameClient::Stop()
{
	if (!_running)
		return;

	_running = false;
	if (_thread.joinable())
		_thread.join();
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

bool PipeFrameClient::ReadExact(HANDLE h, void* buf, DWORD count)
{
	BYTE* p = static_cast<BYTE*>(buf);
	DWORD total = 0;
	while (total < count)
	{
		DWORD got = 0;
		if (!ReadFile(h, p + total, count - total, &got, nullptr) || got == 0)
			return false;
		total += got;
	}
	return true;
}

void PipeFrameClient::Run()
{
	while (_running)
	{
		if (!WaitNamedPipeW(_pipeName.c_str(), 3000))
		{
			Sleep(200);
			continue;
		}

		HANDLE pipe = CreateFileW(_pipeName.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_EXISTING, 0, nullptr);
		if (pipe == INVALID_HANDLE_VALUE)
		{
			Sleep(200);
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
			Sleep(200);
			continue;
		}

		WsConfigMsg cfg{};
		if (!ReadExact(pipe, &cfg, sizeof(cfg)))
		{
			CloseHandle(pipe);
			Sleep(200);
			continue;
		}

		_width = cfg.width;
		_height = cfg.height;
		_fpsNum = cfg.fpsNum;
		_fpsDen = cfg.fpsDen;
		WINTRACE(L"PipeFrameClient config %ux%u format=%u fps=%u/%u", cfg.width, cfg.height, cfg.format, cfg.fpsNum, cfg.fpsDen);

		// Frame loop.
		while (_running)
		{
			if (!ReadExact(pipe, &hdr, sizeof(hdr)))
				break; // disconnected

			if (hdr.magic != WS_MSG_MAGIC)
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
		Sleep(200);
	}
}
