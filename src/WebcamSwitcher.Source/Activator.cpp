#include "pch.h"
#include "Undocumented.h"
#include "Tools.h"
#include "EnumNames.h"
#include "MFTools.h"
#include "PipeFrameClient.h"
#include "MediaStream.h"
#include "MediaSource.h"
#include "Activator.h"

HRESULT Activator::Initialize()
{
	_source = winrt::make_self<MediaSource>();
	RETURN_IF_FAILED(SetUINT32(MF_VIRTUALCAMERA_PROVIDE_ASSOCIATED_CAMERA_SOURCES, 1));
	RETURN_IF_FAILED(SetGUID(MFT_TRANSFORM_CLSID_Attribute, CLSID_WebcamSwitcherSource));
	RETURN_IF_FAILED(_source->Initialize(this));
	return S_OK;
}

// IMFActivate
STDMETHODIMP Activator::ActivateObject(REFIID riid, void** ppv)
{
	WINTRACE(L"Activator::ActivateObject '%s'", GUID_ToStringW(riid).c_str());
	RETURN_HR_IF_NULL(E_POINTER, ppv);
	*ppv = nullptr;

	// use undoc'd frame server property
	UINT32 pid = 0;
	if (SUCCEEDED(GetUINT32(MF_FRAMESERVER_CLIENTCONTEXT_CLIENTPID, &pid)) && pid)
	{
		auto name = GetProcessName(pid);
		if (!name.empty())
		{
			WINTRACE(L"Activator::ActivateObject client process '%s'", name.c_str());
		}
	}
	// The Frame Server sets MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME on `this` at
	// ActivateObject time (never at Initialize). It identifies which virtual
	// camera instance we are, so bind the matching pipe before handing out the
	// source. Absent name => empty string => switcher pipe fallback.
	std::wstring friendlyName;
	{
		LPWSTR raw = nullptr;
		UINT32 len = 0;
		HRESULT hr = GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &raw, &len);
		if (SUCCEEDED(hr) && raw)
		{
			friendlyName = raw;
			CoTaskMemFree(raw);
			raw = nullptr;
		}
		WINTRACE(L"Activator::ActivateObject friendly name '%s' (hr=0x%08X)", friendlyName.c_str(), hr);
	}
	RETURN_IF_FAILED(_source->BindCamera(friendlyName));

	RETURN_IF_FAILED_MSG(_source->QueryInterface(riid, ppv), "Activator::ActivateObject failed on IID %s", GUID_ToStringW(riid).c_str());
	return S_OK;
}

STDMETHODIMP Activator::ShutdownObject()
{
	WINTRACE(L"Activator::ShutdownObject");
	return S_OK;
}

STDMETHODIMP Activator::DetachObject()
{
	WINTRACE(L"Activator::DetachObject");
	_source = nullptr;
	return S_OK;
}
