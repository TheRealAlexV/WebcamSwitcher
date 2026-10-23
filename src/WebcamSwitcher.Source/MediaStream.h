#pragma once
#include <vector>
#include <wil/resource.h>

class PipeFrameClient;

struct MediaStream : winrt::implements<MediaStream, CBaseAttributes<IMFAttributes>, IMFMediaStream2, IKsControl>
{
public:
	// IMFMediaEventGenerator
	STDMETHOD(BeginGetEvent)(IMFAsyncCallback* pCallback, IUnknown* punkState);
	STDMETHOD(EndGetEvent)(IMFAsyncResult* pResult, IMFMediaEvent** ppEvent);
	STDMETHOD(GetEvent)(DWORD dwFlags, IMFMediaEvent** ppEvent);
	STDMETHOD(QueueEvent)(MediaEventType met, REFGUID guidExtendedType, HRESULT hrStatus, const PROPVARIANT* pvValue);

	// IMFMediaStream
	STDMETHOD(GetMediaSource)(IMFMediaSource** ppMediaSource);
	STDMETHOD(GetStreamDescriptor)(IMFStreamDescriptor** ppStreamDescriptor);
	STDMETHOD(RequestSample)(IUnknown* pToken);

	// IMFMediaStream2
	STDMETHOD(SetStreamState)(MF_STREAM_STATE value);
	STDMETHOD(GetStreamState)(MF_STREAM_STATE* value);

	// IKsControl
	STDMETHOD_(NTSTATUS, KsProperty)(PKSPROPERTY Property, ULONG PropertyLength, LPVOID PropertyData, ULONG DataLength, ULONG* BytesReturned);
	STDMETHOD_(NTSTATUS, KsMethod)(PKSMETHOD Method, ULONG MethodLength, LPVOID MethodData, ULONG DataLength, ULONG* BytesReturned);
	STDMETHOD_(NTSTATUS, KsEvent)(PKSEVENT Event, ULONG EventLength, LPVOID EventData, ULONG DataLength, ULONG* BytesReturned);

public:
	MediaStream() :
		_index(0),
		_state(MF_STREAM_STATE_STOPPED),
		_format(GUID_NULL),
		_width(0),
		_height(0),
		_fpsNum(0),
		_fpsDen(1),
		_nextSampleTime(0),
		_client(nullptr)
	{
		SetBaseAttributesTraceName(L"MediaStreamAtts");
	}

	HRESULT Initialize(IMFMediaSource* source, int index);
	void SetFrameClient(PipeFrameClient* client) { _client = client; }
	HRESULT Start(IMFMediaType* type);
	HRESULT Stop();
	void Shutdown();

private:
	// Output samples are recycled instead of being allocated per request:
	// MF's video sample allocator when it hands out CPU-writable samples,
	// otherwise a small ring of preallocated IMFSample/IMFMediaBuffer pairs.
	// Six samples cover the Frame Server plus a client or two in flight.
	static constexpr DWORD kSamplePoolSize = 6;

	// How long a fresh activation waits for its first pipe frame before it starts
	// emitting placeholder samples. Keeps "just started" from looking like EOS.
	static constexpr LONGLONG kFirstFrameWait100ns = 3000000; // 300 ms

	// Called once from Initialize(), outside _lock (nothing can request yet).
	HRESULT CreateSamplePool(IMFMediaType* type);
	// Builds the preallocated ring (also called lazily if the allocator is dropped).
	HRESULT BuildRing(DWORD nv12Size);
	// Returns a sample with its buffer locked and `maxLen` >= NV12 size. Must be
	// called with _lock held: the pool is owned by this stream and only
	// RequestSample touches it.
	// `maxLen` receives the buffer's real capacity. The caller writes at most that
	// many bytes: if the app reconfigures to a larger frame than this stream's
	// descriptor, the request degrades to a short frame instead of failing - a
	// failing RequestSample puts the whole stream into an error state that only a
	// Frame Server restart recovers from.
	HRESULT AcquireSample(wil::com_ptr_nothrow<IMFSample>& sample, wil::com_ptr_nothrow<IMFMediaBuffer>& buffer, BYTE** data, DWORD* maxLen);
	// Fills NV12 Y=0x10 / UV=0x80 from `from` to `size`, leaving any real pixels
	// already copied below `from` alone.
	void FillPlaceholder(BYTE* data, DWORD from, DWORD size) const;

#if _DEBUG
	int32_t query_interface_tearoff(winrt::guid const& id, void** object) const noexcept override
	{
		RETURN_HR_MSG(E_NOINTERFACE, "MediaStream QueryInterface failed on IID %s", GUID_ToStringW(id).c_str());
	}
#endif

	winrt::slim_mutex  _lock;
	MF_STREAM_STATE _state;
	GUID _format;
	UINT32 _width;
	UINT32 _height;
	UINT32 _fpsNum;
	UINT32 _fpsDen;
	LONGLONG _nextSampleTime;
	// MFGetSystemTime() at Start(); 0 while the stream is not running. Used to
	// bound the wait for the first frame of a new activation.
	LONGLONG _startTime = 0;
	// Manual-reset event signaled by Stop()/Shutdown() to interrupt the
	// RequestSample pacing wait (see MediaStream::RequestSample).
	wil::unique_handle _sampleWake;
	wil::com_ptr_nothrow<IMFStreamDescriptor> _descriptor;
	wil::com_ptr_nothrow<IMFMediaEventQueue> _queue;
	wil::com_ptr_nothrow<IMFMediaSource> _source;
	PipeFrameClient* _client;   // not owned
	int _index;

	// Sample pool. _sampleAllocator is empty after a fallback; _pooledSamples is
	// then the ring used instead. At least one of them normally exists, but
	// RequestSample still copes with both being empty (per-sample allocation).
	wil::com_ptr_nothrow<IMFVideoSampleAllocatorEx> _sampleAllocator;
	std::vector<wil::com_ptr_nothrow<IMFSample>> _pooledSamples;
	size_t _nextPooledSample = 0;
	DWORD _nv12Size = 0;
	// One-shot diagnostics, so a pool that cannot serve requests is reported
	// once rather than per frame.
	bool _allocatorEmptyLogged = false;
	bool _allocatorDroppedLogged = false;
	bool _shortBufferLogged = false;

	// One flag per distinct cause of a degraded (never failing) request.
	bool _noGeometryLogged = false;
	bool _bufferAllocFailedLogged = false;
	bool _requestWhileStoppedLogged = false;
	bool _emergencySampleLogged = false;
	bool _noSampleAtAllLogged = false;
	bool _unlockFailedLogged = false;
	bool _setLengthFailedLogged = false;
	bool _tokenFailedLogged = false;
	bool _queueSampleFailedLogged = false;
	bool _startTypeLogged = false;
	bool _startEventLogged = false;
	bool _stopEventLogged = false;

	// Last-resort sample owned by the stream (see RequestSample).
	wil::com_ptr_nothrow<IMFSample> _emergencySample;
};
