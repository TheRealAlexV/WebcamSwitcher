#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "PipeFrameClient.h"
#include "MediaStream.h"
#include "MediaSource.h"
#include "WebcamSwitcherProtocol.h"

HRESULT MediaStream::Initialize(IMFMediaSource* source, int index)
{
	RETURN_HR_IF_NULL(E_POINTER, source);
	_source = source;
	_index = index;

	RETURN_IF_FAILED(SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_STREAM_ID, index));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1));
	RETURN_IF_FAILED(SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes::MFFrameSourceTypes_Color));

	RETURN_IF_FAILED(MFCreateEventQueue(&_queue));

	// Advertise a single fixed NV12 media type.
	wil::com_ptr_nothrow<IMFMediaType> nv12Type;
	RETURN_IF_FAILED(MFCreateMediaType(&nv12Type));
	nv12Type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	nv12Type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
	nv12Type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
	nv12Type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
	MFSetAttributeSize(nv12Type.get(), MF_MT_FRAME_SIZE, WS_DEFAULT_WIDTH, WS_DEFAULT_HEIGHT);
	nv12Type->SetUINT32(MF_MT_DEFAULT_STRIDE, WS_DEFAULT_WIDTH);
	MFSetAttributeRatio(nv12Type.get(), MF_MT_FRAME_RATE, WS_DEFAULT_FPS_NUM, WS_DEFAULT_FPS_DEN);
	UINT64 bitrate = (UINT64)WS_DEFAULT_WIDTH * WS_DEFAULT_HEIGHT * 3 / 2 * 8 * WS_DEFAULT_FPS_NUM / WS_DEFAULT_FPS_DEN;
	nv12Type->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)bitrate);
	MFSetAttributeRatio(nv12Type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

	IMFMediaType* types[] = { nv12Type.get() };
	RETURN_IF_FAILED_MSG(MFCreateStreamDescriptor(_index, 1, types, &_descriptor), "MFCreateStreamDescriptor failed");

	wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
	RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
	RETURN_IF_FAILED(handler->SetCurrentMediaType(nv12Type.get()));

	return S_OK;
}

HRESULT MediaStream::Start(IMFMediaType* type)
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	if (type)
	{
		RETURN_IF_FAILED(type->GetGUID(MF_MT_SUBTYPE, &_format));
		WINTRACE(L"MediaStream::Start format: %s", GUID_ToStringW(_format).c_str());
	}

	RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr));
	_state = MF_STREAM_STATE_RUNNING;
	return S_OK;
}

HRESULT MediaStream::Stop()
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr));
	_state = MF_STREAM_STATE_STOPPED;
	return S_OK;
}

void MediaStream::Shutdown()
{
	if (_queue)
	{
		LOG_IF_FAILED_MSG(_queue->Shutdown(), "Queue shutdown failed");
		_queue.reset();
	}

	_descriptor.reset();
	_source.reset();
	_attributes.reset();
}

// IMFMediaEventGenerator
STDMETHODIMP MediaStream::BeginGetEvent(IMFAsyncCallback* pCallback, IUnknown* punkState)
{
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->BeginGetEvent(pCallback, punkState));
	return S_OK;
}

STDMETHODIMP MediaStream::EndGetEvent(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent)
{
	RETURN_HR_IF_NULL(E_POINTER, ppEvent);
	*ppEvent = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->EndGetEvent(pResult, ppEvent));
	return S_OK;
}

STDMETHODIMP MediaStream::GetEvent(DWORD dwFlags, IMFMediaEvent** ppEvent)
{
	RETURN_HR_IF_NULL(E_POINTER, ppEvent);
	*ppEvent = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->GetEvent(dwFlags, ppEvent));
	return S_OK;
}

STDMETHODIMP MediaStream::QueueEvent(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue)
{
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	RETURN_IF_FAILED(_queue->QueueEventParamVar(met, guidExtendedType, hrStatus, pvValue));
	return S_OK;
}

// IMFMediaStream
STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** ppMediaSource)
{
	RETURN_HR_IF_NULL(E_POINTER, ppMediaSource);
	*ppMediaSource = nullptr;
	RETURN_HR_IF(MF_E_SHUTDOWN, !_source);

	RETURN_IF_FAILED(_source.copy_to(ppMediaSource));
	return S_OK;
}

STDMETHODIMP MediaStream::GetStreamDescriptor(IMFStreamDescriptor** ppStreamDescriptor)
{
	RETURN_HR_IF_NULL(E_POINTER, ppStreamDescriptor);
	*ppStreamDescriptor = nullptr;
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_descriptor);

	RETURN_IF_FAILED(_descriptor.copy_to(ppStreamDescriptor));
	return S_OK;
}

STDMETHODIMP MediaStream::RequestSample(IUnknown* pToken)
{
	winrt::slim_lock_guard lock(_lock);
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	const DWORD width = WS_DEFAULT_WIDTH;
	const DWORD height = WS_DEFAULT_HEIGHT;
	const DWORD ySize = width * height;
	const DWORD nv12Size = ySize + ySize / 2;

	wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
	RETURN_IF_FAILED(MFCreateMemoryBuffer(nv12Size, &buffer));

	BYTE* data = nullptr;
	DWORD maxLen = 0;
	DWORD curLen = 0;
	RETURN_IF_FAILED(buffer->Lock(&data, &maxLen, &curLen));

	// Start with black.
	memset(data, 0, maxLen);

	if (_client)
	{
		std::vector<BYTE> frame;
		WsFrameInfo info{};
		if (_client->CopyLatestFrame(frame, info))
		{
			DWORD copyBytes = (DWORD)std::min<size_t>(frame.size(), nv12Size);
			if (copyBytes)
				memcpy(data, frame.data(), copyBytes);
		}
	}

	RETURN_IF_FAILED(buffer->Unlock());
	RETURN_IF_FAILED(buffer->SetCurrentLength(nv12Size));

	wil::com_ptr_nothrow<IMFSample> sample;
	RETURN_IF_FAILED(MFCreateSample(&sample));
	RETURN_IF_FAILED(sample->AddBuffer(buffer.get()));

	RETURN_IF_FAILED(sample->SetSampleTime(MFGetSystemTime()));
	RETURN_IF_FAILED(sample->SetSampleDuration((LONGLONG)(_client ? _client->FrameDuration100ns() : 333333)));

	if (pToken)
	{
		RETURN_IF_FAILED(sample->SetUnknown(MFSampleExtension_Token, pToken));
	}

	RETURN_IF_FAILED(_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.get()));
	return S_OK;
}

// IMFMediaStream2
STDMETHODIMP MediaStream::SetStreamState(MF_STREAM_STATE value)
{
	if (_state == value)
		return S_OK;
	switch (value)
	{
	case MF_STREAM_STATE_PAUSED:
		if (_state != MF_STREAM_STATE_RUNNING)
			RETURN_HR(MF_E_INVALID_STATE_TRANSITION);

		_state = value;
		break;

	case MF_STREAM_STATE_RUNNING:
		RETURN_IF_FAILED(Start(nullptr));
		break;

	case MF_STREAM_STATE_STOPPED:
		RETURN_IF_FAILED(Stop());
		break;

	default:
		RETURN_HR(MF_E_INVALID_STATE_TRANSITION);
		break;
	}
	return S_OK;
}

STDMETHODIMP MediaStream::GetStreamState(MF_STREAM_STATE* value)
{
	RETURN_HR_IF_NULL(E_POINTER, value);
	*value = _state;
	return S_OK;
}

// IKsControl
STDMETHODIMP_(NTSTATUS) MediaStream::KsProperty(PKSPROPERTY property, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	RETURN_HR_IF_NULL(E_POINTER, property);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsProperty prop:%s", PKSIDENTIFIER_ToString(property, length).c_str());

	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsMethod(PKSMETHOD method, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	RETURN_HR_IF_NULL(E_POINTER, method);
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsMethod method:%s", PKSIDENTIFIER_ToString(method, length).c_str());

	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}

STDMETHODIMP_(NTSTATUS) MediaStream::KsEvent(PKSEVENT evt, ULONG length, LPVOID data, ULONG dataLength, ULONG* bytesReturned)
{
	RETURN_HR_IF_NULL(E_POINTER, bytesReturned);
	winrt::slim_lock_guard lock(_lock);

	WINTRACE(L"MediaStream::KsEvent event:%s", PKSIDENTIFIER_ToString(evt, length).c_str());
	return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND);
}
