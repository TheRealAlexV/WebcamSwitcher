#include "pch.h"
#include <mfapi.h>
#include <mfvirtualcamera.h>

#pragma comment(lib, "mfplat")
#pragma comment(lib, "mfuuid")

// Thin C bridge so the managed app can register/start/stop the virtual camera
// without hand-writing COM interop for IMFVirtualCamera.

extern "C" __declspec(dllexport) HRESULT WebcamSwitcher_Start(
	_In_ LPCWSTR friendlyName,
	_In_ LPCWSTR sourceId,
	_Out_ void** handle)
{
	if (!friendlyName || !sourceId || !handle)
		return E_INVALIDARG;

	*handle = nullptr;

	HRESULT hr = MFStartup(MF_VERSION);
	if (FAILED(hr) && hr != MF_E_ALREADY_INITIALIZED)
		return hr;

	IMFVirtualCamera* vcam = nullptr;
	hr = MFCreateVirtualCamera(
		MFVirtualCameraType_SoftwareCameraSource,
		MFVirtualCameraLifetime_Session,
		MFVirtualCameraAccess_CurrentUser,
		friendlyName,
		sourceId,
		nullptr, 0,
		&vcam);

	if (SUCCEEDED(hr))
	{
		hr = vcam->Start(nullptr);
		if (FAILED(hr))
		{
			vcam->Release();
			vcam = nullptr;
		}
	}

	*handle = vcam;
	return hr;
}

extern "C" __declspec(dllexport) void WebcamSwitcher_Stop(_In_opt_ void* handle)
{
	auto* vcam = static_cast<IMFVirtualCamera*>(handle);
	if (vcam)
	{
		vcam->Stop();
		vcam->Shutdown();
		vcam->Release();
	}
	// NOTE: deliberately no MFShutdown() here. The app may still be capturing
	// with MediaCapture, and shutting down the MF platform underneath it is
	// unsafe. The platform is left initialized for the process lifetime.
}
