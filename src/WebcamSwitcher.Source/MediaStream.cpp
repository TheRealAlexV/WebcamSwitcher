#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "PipeFrameClient.h"
#include "SourceFormat.h"
#include "MediaStream.h"
#include "MediaSource.h"
#include "WebcamSwitcherProtocol.h"

namespace
{
	// A failing RequestSample is latched by the Frame Server for the life of the
	// process (the source object is cached per device), so the sample path never
	// returns a failure. Anomalies are reported once per cause instead: WINTRACE
	// compiles to __noop in Release, hence the OutputDebugStringW so a recurrence
	// is still visible on a customer machine.
	void TraceAnomalyOnce(bool& flag, const wchar_t* message)
	{
		if (flag)
			return;
		flag = true;
		WINTRACE(L"MediaStream anomaly: %s", message);
		::OutputDebugStringW(L"WebcamSwitcher MediaStream anomaly: ");
		::OutputDebugStringW(message);
		::OutputDebugStringW(L"\n");
	}

	// When pacing to the real clock, resync _nextSampleTime instead of
	// accumulating drift once the source is this many frame periods behind.
	constexpr LONGLONG kMaxSampleLagFrames = 3;
}

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

	// Manual-reset, initially non-signaled: SetEvent() from Stop()/Shutdown()
	// interrupts a RequestSample that is waiting for the next frame to be due.
	_sampleWake.reset(CreateEventW(nullptr, TRUE, FALSE, nullptr));
	RETURN_LAST_ERROR_IF_NULL(_sampleWake.get());

	// Read the configured output format (persisted by the app) and advertise it.
	auto fmt = ReadSourceFormat();
	_width = fmt.width;
	_height = fmt.height;
	_fpsNum = fmt.fpsNum;
	_fpsDen = fmt.fpsDen;

	// Advertise a single NV12 media type at the configured size/fps.
	wil::com_ptr_nothrow<IMFMediaType> nv12Type;
	RETURN_IF_FAILED(MFCreateMediaType(&nv12Type));
	nv12Type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
	nv12Type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
	nv12Type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
	nv12Type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
	MFSetAttributeSize(nv12Type.get(), MF_MT_FRAME_SIZE, _width, _height);
	nv12Type->SetUINT32(MF_MT_DEFAULT_STRIDE, _width);
	MFSetAttributeRatio(nv12Type.get(), MF_MT_FRAME_RATE, _fpsNum, _fpsDen);
	UINT64 bitrate = (UINT64)_width * _height * 3 / 2 * 8 * _fpsNum / _fpsDen;
	nv12Type->SetUINT32(MF_MT_AVG_BITRATE, (UINT32)bitrate);
	nv12Type->SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
	MFSetAttributeRatio(nv12Type.get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

	IMFMediaType* types[] = { nv12Type.get() };
	RETURN_IF_FAILED_MSG(MFCreateStreamDescriptor(_index, 1, types, &_descriptor), "MFCreateStreamDescriptor failed");

	wil::com_ptr_nothrow<IMFMediaTypeHandler> handler;
	RETURN_IF_FAILED(_descriptor->GetMediaTypeHandler(&handler));
	RETURN_IF_FAILED(handler->SetCurrentMediaType(nv12Type.get()));

	// Build the recycled-sample pool from the very type object advertised above.
	// This never fails: if no pool can be built, RequestSample keeps allocating
	// per sample (the pre-optimization behaviour).
	RETURN_IF_FAILED(CreateSamplePool(nv12Type.get()));

	return S_OK;
}

// Builds the preallocated ring. Used at Initialize when no allocator can serve
// this stream, and lazily if the allocator later turns out to be unusable.
HRESULT MediaStream::BuildRing(DWORD nv12Size)
{
	for (DWORD i = 0; i < kSamplePoolSize; i++)
	{
		wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
		wil::com_ptr_nothrow<IMFSample> sample;
		RETURN_IF_FAILED(MFCreateMemoryBuffer(nv12Size, &buffer));
		RETURN_IF_FAILED(MFCreateSample(&sample));
		RETURN_IF_FAILED(sample->AddBuffer(buffer.get()));
		_pooledSamples.push_back(std::move(sample));
	}
	_nextPooledSample = 0;
	return S_OK;
}

HRESULT MediaStream::CreateSamplePool(IMFMediaType* type)
{
	RETURN_HR_IF_NULL(E_POINTER, type);

	const DWORD ySize = _width * _height;
	_nv12Size = ySize + ySize / 2;
	RETURN_HR_IF(E_INVALIDARG, _nv12Size == 0);

	// Preferred: MF's video sample allocator hands out reusable samples, so a
	// request no longer allocates (and page-faults in) a 1.4 MB heap block.
	wil::com_ptr_nothrow<IMFVideoSampleAllocatorEx> allocator;
	HRESULT hr = MFCreateVideoSampleAllocatorEx(IID_PPV_ARGS(&allocator));
	if (SUCCEEDED(hr) && allocator)
		hr = allocator->InitializeSampleAllocator(kSamplePoolSize, type);

	if (SUCCEEDED(hr) && allocator)
	{
		// Validate once, now: an allocator may hand out samples backed by memory
		// this source cannot map, and discovering that per frame would cost more
		// than the pool saves.
		wil::com_ptr_nothrow<IMFSample> probe;
		wil::com_ptr_nothrow<IMFMediaBuffer> probeBuffer;
		BYTE* probeData = nullptr;
		DWORD probeMaxLen = 0;
		DWORD probeLength = 0;
		bool usable = false;
		if (SUCCEEDED(allocator->AllocateSample(&probe)) && probe &&
			SUCCEEDED(probe->GetBufferByIndex(0, &probeBuffer)) && probeBuffer &&
			SUCCEEDED(probeBuffer->Lock(&probeData, &probeMaxLen, &probeLength)))
		{
			usable = probeData && probeMaxLen >= _nv12Size;
			LOG_IF_FAILED(probeBuffer->Unlock());
		}

		if (usable)
		{
			_sampleAllocator = std::move(allocator);
			WINTRACE(L"MediaStream: sample pool via MFCreateVideoSampleAllocatorEx (%ux%u, %u bytes, %u samples)",
				_width, _height, _nv12Size, kSamplePoolSize);
			return S_OK;
		}
		WINTRACE(L"MediaStream: MF video sample allocator unusable; preallocating a sample ring instead");
	}
	else
	{
		WINTRACE(L"MediaStream: MFCreateVideoSampleAllocatorEx failed (hr=0x%08X); preallocating a sample ring instead", hr);
	}

	// Fallback: a small ring of preallocated samples. At most six 1.4 MB buffers
	// are held for the stream's lifetime, which is the point - no per-frame heap
	// traffic. If even this fails, RequestSample allocates per sample as before.
	_pooledSamples.reserve(kSamplePoolSize);
	for (DWORD i = 0; i < kSamplePoolSize; i++)
	{
		wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
		wil::com_ptr_nothrow<IMFSample> sample;
		hr = MFCreateMemoryBuffer(_nv12Size, &buffer);
		if (SUCCEEDED(hr))
			hr = MFCreateSample(&sample);
		if (SUCCEEDED(hr))
			hr = sample->AddBuffer(buffer.get());
		if (FAILED(hr))
			break;
		_pooledSamples.push_back(std::move(sample));
	}
	WINTRACE(L"MediaStream: preallocated ring of %zu NV12 samples (%ux%u, %u bytes)",
		_pooledSamples.size(), _width, _height, _nv12Size);
	return S_OK;
}

// Never fails: on any problem it returns S_OK with `sample` empty, and
// RequestSample falls back to the emergency sample. A failure HRESULT from here
// used to travel up through RequestSample and end the stream for good.
HRESULT MediaStream::AcquireSample(wil::com_ptr_nothrow<IMFSample>& sample,
	wil::com_ptr_nothrow<IMFMediaBuffer>& buffer, BYTE** data, DWORD* maxLen)
{
	*data = nullptr;
	*maxLen = 0;
	// Same fallback as RequestSample: only reachable if Initialize never got to
	// build a pool (e.g. the stream is started despite a failed Initialize).
	const DWORD nv12Size = _nv12Size ? _nv12Size : (_width * _height * 3 / 2);
	if (nv12Size == 0)
	{
		// No usable geometry: report "no sample" instead of failing upward.
		TraceAnomalyOnce(_noGeometryLogged, L"stream has no frame geometry; AcquireSample reports no sample");
		return S_OK;
	}

	if (_sampleAllocator)
	{
		wil::com_ptr_nothrow<IMFSample> pooled;
		HRESULT hr = _sampleAllocator->AllocateSample(&pooled);
		if (SUCCEEDED(hr) && pooled)
		{
			wil::com_ptr_nothrow<IMFMediaBuffer> pooledBuffer;
			BYTE* pooledData = nullptr;
			DWORD pooledMaxLen = 0;
			DWORD pooledLength = 0;
			if (SUCCEEDED(pooled->GetBufferByIndex(0, &pooledBuffer)) && pooledBuffer &&
				SUCCEEDED(pooledBuffer->Lock(&pooledData, &pooledMaxLen, &pooledLength)))
			{
				if (pooledData && pooledMaxLen >= nv12Size)
				{
					sample = std::move(pooled);
					buffer = std::move(pooledBuffer);
					*data = pooledData;
					*maxLen = pooledMaxLen;
					return S_OK;
				}
				LOG_IF_FAILED(pooledBuffer->Unlock());
			}

			// The allocator cannot serve this stream (unexpected memory backing).
			// Stop using it for good and fall through to the ring, so a broken
			// allocator cannot produce a placeholder-only stream.
			if (!_allocatorDroppedLogged)
			{
				_allocatorDroppedLogged = true;
				WINTRACE(L"MediaStream: pooled allocator returned unusable memory; using the sample ring from now on");
			}
			_sampleAllocator.reset();
		}
		else if (hr == MF_E_SAMPLEALLOCATOR_EMPTY)
		{
			// Every pooled sample is still held downstream (a paused consumer, a
			// Frame Server that has not drained its queue, more clients than
			// samples). Measured on this machine: AllocateSample then takes ~1 s
			// before reporting EMPTY - unacceptable inside a request, it stalls
			// the whole stream and fights the pacing. Switch to the ring for good;
			// it is the same preallocated memory without the blocking allocator.
			TraceAnomalyOnce(_allocatorEmptyLogged, L"pooled allocator exhausted; switching to the sample ring permanently");
			_sampleAllocator.reset();
		}
		else
		{
			if (!_allocatorDroppedLogged)
			{
				_allocatorDroppedLogged = true;
				WINTRACE(L"MediaStream: AllocateSample failed (hr=0x%08X); using the sample ring from now on", hr);
			}
			_sampleAllocator.reset();
		}
	}

	// Preallocated ring: hand samples out in order, so a sample is only reused
	// after the whole ring has cycled (6 frames = 200 ms at 30 fps).
	if (_pooledSamples.empty())
	{
		// Lazily built when the allocator was dropped after Initialize.
		if (FAILED(BuildRing(nv12Size)))
			TraceAnomalyOnce(_bufferAllocFailedLogged, L"could not build the sample ring");
	}

	if (!_pooledSamples.empty())
	{
		const size_t index = _nextPooledSample % _pooledSamples.size();
		_nextPooledSample = index + 1;
		sample = _pooledSamples[index];
	}
	else
	{
		// No pool at all: allocate per sample (allocation failure is reported as
		// "no sample", never as a failed request).
		if (FAILED(MFCreateMemoryBuffer(nv12Size, &buffer)) || !buffer)
		{
			TraceAnomalyOnce(_bufferAllocFailedLogged, L"per-sample buffer allocation failed");
			sample.reset();
			return S_OK;
		}
		if (FAILED(MFCreateSample(&sample)) || !sample)
		{
			TraceAnomalyOnce(_bufferAllocFailedLogged, L"per-sample sample allocation failed");
			sample.reset();
			buffer.reset();
			return S_OK;
		}
		LOG_IF_FAILED(sample->AddBuffer(buffer.get()));
	}

	if (!sample || !buffer)
	{
		TraceAnomalyOnce(_bufferAllocFailedLogged, L"sample ring entry is empty");
		sample.reset();
		buffer.reset();
		return S_OK;
	}

	if (FAILED(sample->GetBufferByIndex(0, &buffer)) || !buffer)
	{
		TraceAnomalyOnce(_bufferAllocFailedLogged, L"sample has no buffer");
		sample.reset();
		return S_OK;
	}

	BYTE* locked = nullptr;
	DWORD lockedMaxLen = 0;
	DWORD curLen = 0;
	if (FAILED(buffer->Lock(&locked, &lockedMaxLen, &curLen)) || !locked)
	{
		TraceAnomalyOnce(_bufferAllocFailedLogged, L"sample buffer did not map");
		sample.reset();
		buffer.reset();
		return S_OK;
	}
	if (lockedMaxLen < nv12Size && !_shortBufferLogged)
	{
		// Only reachable when the pipe's frame size differs from this stream's
		// descriptor. Serve a short frame rather than failing the request.
		_shortBufferLogged = true;
		WINTRACE(L"MediaStream: sample buffer %u < NV12 size %u; writing a short frame", lockedMaxLen, nv12Size);
	}
	*data = locked;
	*maxLen = lockedMaxLen;
	return S_OK;
}

void MediaStream::FillPlaceholder(BYTE* data, DWORD from, DWORD size) const
{
	const DWORD ySize = _width * _height;
	if (from > size)
		from = size;

	// Legal NV12 black is Y=16, U=V=128. Only the part not already filled with a
	// real frame is written: pooled memory is recycled, so it can never be
	// assumed to hold the placeholder already.
	if (from < ySize)
		memset(data + from, 0x10, ySize - from);            // Y plane = 16
	const DWORD uvStart = std::max<DWORD>(from, ySize);
	if (uvStart < size)
		memset(data + uvStart, 0x80, size - uvStart);        // interleaved U/V = 128
}

HRESULT MediaStream::Start(IMFMediaType* type)
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	if (type)
	{
		GUID format = GUID_NULL;
		if (SUCCEEDED(type->GetGUID(MF_MT_SUBTYPE, &format)))
		{
			_format = format;
			WINTRACE(L"MediaStream::Start format: %s", GUID_ToStringW(_format).c_str());
		}
		else
		{
			// A type without a subtype during a graph rebuild must not fail the
			// activation: keep the format negotiated at Initialize.
			TraceAnomalyOnce(_startTypeLogged, L"stream Start type has no subtype; keeping the previous format");
		}
	}

	// Clear a wake left over from a previous Stop()/Shutdown() before the state
	// becomes RUNNING, so a RequestSample cannot observe RUNNING with a stale wake.
	if (_sampleWake)
		ResetEvent(_sampleWake.get());

	{
		winrt::slim_lock_guard lock(_lock);
		_nextSampleTime = MFGetSystemTime();
		_startTime = _nextSampleTime;
		_state = MF_STREAM_STATE_RUNNING;
	}

	// A failed event post would mean the consumer never sees MEStreamStarted, but
	// returning a failure here fails the whole activation and is latched.
	if (FAILED(_queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, nullptr)))
		TraceAnomalyOnce(_startEventLogged, L"queueing MEStreamStarted failed");
	return S_OK;
}

HRESULT MediaStream::Stop()
{
	RETURN_HR_IF(MF_E_SHUTDOWN, !_queue);

	// Abandon a RequestSample that is currently pacing: the stream is stopping.
	if (_sampleWake)
		SetEvent(_sampleWake.get());

	{
		winrt::slim_lock_guard lock(_lock);
		_startTime = 0;
		_state = MF_STREAM_STATE_STOPPED;
	}

	if (FAILED(_queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr)))
		TraceAnomalyOnce(_stopEventLogged, L"queueing MEStreamStopped failed");
	return S_OK;
}

void MediaStream::Shutdown()
{
	// Wake a pacing RequestSample so it observes the shutdown below.
	if (_sampleWake)
		SetEvent(_sampleWake.get());

	if (_queue)
	{
		LOG_IF_FAILED_MSG(_queue->Shutdown(), "Queue shutdown failed");
		_queue.reset();
	}

	_descriptor.reset();
	_source.reset();
	_attributes.reset();

	// Release the recycled samples with the stream. Nothing touches the pool
	// outside RequestSample's _lock, and a post-shutdown RequestSample bails out
	// on the missing queue before it can reach it, so taking _lock here cannot
	// deadlock with a pacing caller.
	{
		winrt::slim_lock_guard lock(_lock);
		_sampleAllocator.reset();
		_pooledSamples.clear();
		_nextPooledSample = 0;
	}
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
	// Pace to the real clock. This is a live source (MFMEDIASOURCE_IS_LIVE), so
	// the Frame Server pulls as fast as we answer; answering immediately spins a
	// core per virtual camera even with no client streaming. Hold the sample back
	// until its timestamp is due, sleeping on a cancelable event that Stop() and
	// Shutdown() signal, and resync instead of accumulating drift when we have
	// fallen several frames behind.
	//
	// This method must never return a failure while the stream object lives: the
	// Frame Server caches the media source per device, and one failed
	// RequestSample (a rapid Stop/Start from an OBS graph rebuild is enough)
	// kills that virtual camera for the life of the process. Every internal
	// problem therefore degrades to a placeholder sample and S_OK.
	const HANDLE wake = _sampleWake.get();
	LONGLONG duration = 333333; // 30 fps fallback
	LONGLONG sampleTime = 0;

	for (;;)
	{
		DWORD waitMs = 0;
		bool stopped = false;
		{
			winrt::slim_lock_guard lock(_lock);
			if (!_queue)
			{
				// The only legitimate failure: the object has been shut down, so
				// there is no event queue to post to and MF will not ask again.
				return MF_E_SHUTDOWN;
			}

			if (_fpsNum > 0 && _fpsDen > 0)
				duration = (LONGLONG)10000000 * _fpsDen / _fpsNum;

			// Stop() during a rebuild: keep answering with paced placeholders
			// instead of failing; the stream may be started again at any moment.
			stopped = (_state != MF_STREAM_STATE_RUNNING);
			if (stopped)
				TraceAnomalyOnce(_requestWhileStoppedLogged, L"RequestSample while the stream is not running; serving a placeholder");

			const LONGLONG now = MFGetSystemTime();

			// Far behind (busy machine, stalled client, first request): resync to
			// the clock instead of producing a burst of catch-up frames.
			if (_nextSampleTime <= 0 || _nextSampleTime < now - duration * kMaxSampleLagFrames)
				_nextSampleTime = now;

			sampleTime = _nextSampleTime;

			// A fresh activation has not received a frame yet. Wait a short,
			// bounded time for the pipe instead of answering with a burst of
			// placeholder samples, which a consumer reads as "no signal". Once
			// the window elapses - or a frame arrives - this stops applying.
			const bool waitFirstFrame =
				_startTime > 0 && _client && !_client->HasFrame() &&
				(now - _startTime) < kFirstFrameWait100ns;

			if (now >= sampleTime && !waitFirstFrame)
				break; // due now: produce the sample below

			if (waitFirstFrame && now >= sampleTime)
			{
				waitMs = 5;
			}
			else
			{
				// Bounded wait: never longer than one frame period, never under _lock.
				const LONGLONG wait100ns = std::min<LONGLONG>(sampleTime - now, duration);
				waitMs = (DWORD)((wait100ns + 9999) / 10000);
			}
		}

		if (wake)
		{
			if (WaitForSingleObject(wake, waitMs > 0 ? waitMs : 1) == WAIT_OBJECT_0)
			{
				// Stop()/Shutdown() interrupted the wait. Do not abandon the
				// request: re-check the state and, if the stream is stopping,
				// fall through to a paced placeholder. Only a real shutdown
				// (queue gone) ends the request, above.
				winrt::slim_lock_guard lock(_lock);
				if (!_queue)
					return MF_E_SHUTDOWN;
				ResetEvent(wake); // consume the wake; pacing resumes at the top
			}
		}
		else
		{
			// Event creation failed at Initialize time: bounded plain sleep, the
			// shutdown state is re-checked at the top of the loop.
			Sleep(waitMs > 0 ? waitMs : 1);
		}
	}

	winrt::slim_lock_guard lock(_lock);
	if (!_queue)
		return MF_E_SHUTDOWN;

	const DWORD width = _width;
	const DWORD height = _height;
	const DWORD ySize = width * height;
	DWORD nv12Size = _nv12Size ? _nv12Size : ySize + ySize / 2;
	if (nv12Size == 0)
		nv12Size = 6; // 2x2 NV12: no geometry known, still produce a valid sample

	wil::com_ptr_nothrow<IMFSample> sample;
	wil::com_ptr_nothrow<IMFMediaBuffer> buffer;
	BYTE* data = nullptr;
	DWORD capacity = 0;
	LOG_IF_FAILED(AcquireSample(sample, buffer, &data, &capacity));

	// Last resort: one sample kept for the lifetime of the stream. Never fails
	// upward even if it cannot be created (allocation failure) - in that case the
	// request is answered without a sample after waiting one frame interval, and
	// the Frame Server will pull again.
	if (!sample || !data)
	{
		if (!_emergencySample)
		{
			wil::com_ptr_nothrow<IMFMediaBuffer> emergencyBuffer;
			if (SUCCEEDED(MFCreateMemoryBuffer(nv12Size, &emergencyBuffer)) && emergencyBuffer &&
				SUCCEEDED(MFCreateSample(&_emergencySample)) && _emergencySample)
			{
				if (FAILED(_emergencySample->AddBuffer(emergencyBuffer.get())))
					_emergencySample.reset();
			}
			else
			{
				_emergencySample.reset();
			}
			if (_emergencySample)
				TraceAnomalyOnce(_emergencySampleLogged, L"serving requests from the emergency sample");
			else
				TraceAnomalyOnce(_noSampleAtAllLogged, L"no sample could be produced; answering without one");
		}

		if (_emergencySample)
		{
			sample = _emergencySample;
			if (SUCCEEDED(sample->GetBufferByIndex(0, &buffer)) && buffer &&
				SUCCEEDED(buffer->Lock(&data, &capacity, nullptr)) && data)
			{
				// locked below
			}
			else
			{
				sample.reset();
				data = nullptr;
			}
		}

		if (!sample || !data)
		{
			// Cannot even borrow the emergency sample: keep pacing and answer
			// with no sample rather than failing the request.
			_nextSampleTime = sampleTime + duration;
			return S_OK;
		}
	}

	// Never write past the buffer: a pooled sample can be smaller than the frame
	// if the pipe reconfigured. Clamping keeps RequestSample from failing, which
	// would end the stream for the life of the Frame Server process.
	const DWORD writeSize = capacity ? std::min<DWORD>(nv12Size, capacity) : nv12Size;

	// One copy: from the pipe client's latest-frame slot straight into the
	// sample's already-mapped memory (no temporary vector, unlike before).
	DWORD copied = 0;
	bool haveFrame = false;
	if (_client)
	{
		WsFrameInfo info{};
		haveFrame = _client->CopyLatestFrameInto(data, writeSize, info, &copied);
	}

	// Placeholder where the pipe delivered nothing.
	if (!haveFrame || copied < writeSize)
		FillPlaceholder(data, haveFrame ? copied : 0, writeSize);

	if (FAILED(buffer->Unlock()))
		TraceAnomalyOnce(_unlockFailedLogged, L"sample buffer unlock failed");
	if (FAILED(buffer->SetCurrentLength(writeSize)))
		TraceAnomalyOnce(_setLengthFailedLogged, L"SetCurrentLength failed; the sample keeps its previous length");

	(void)sample->SetSampleTime(sampleTime);
	(void)sample->SetSampleDuration(duration);
	_nextSampleTime = sampleTime + duration;

	if (pToken)
	{
		if (FAILED(sample->SetUnknown(MFSampleExtension_Token, pToken)))
			TraceAnomalyOnce(_tokenFailedLogged, L"storing the request token failed");
	}
	else
	{
		// A recycled sample can still carry the token of its previous request.
		sample->DeleteItem(MFSampleExtension_Token);
	}

	// QueueEventParamUnk is the last thing that can fail; a stream that cannot
	// post is still better off returning S_OK than latching an error state.
	if (FAILED(_queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.get())))
		TraceAnomalyOnce(_queueSampleFailedLogged, L"queueing the sample event failed");

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
	winrt::slim_lock_guard lock(_lock);
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
