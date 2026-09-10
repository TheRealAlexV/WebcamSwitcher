#pragma once

#define WIN32_LEAN_AND_MEAN             // Exclude rarely-used stuff from Windows headers

#define _CRTDBG_MAP_ALLOC
#include <cstdlib>
#include <crtdbg.h>
#ifdef _DEBUG
#define DBG_NEW new ( _NORMAL_BLOCK , __FILE__ , __LINE__ )
// replace _NORMAL_BLOCK with _CLIENT_BLOCK if you want the allocations to be of _CLIENT_BLOCK type
#else
#define DBG_NEW new
#endif

// Windows Header Files
#include <windows.h>
#include <evntprov.h>
#include <strsafe.h>
#include <initguid.h>
#include <propvarutil.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfvirtualcamera.h>
#include <mferror.h>
#include <mfcaptureengine.h>
#include <ks.h>
#include <ksproxy.h>
#include <ksmedia.h>
#include <dxgi.h>
#include <d3d11.h>
#include <d2d1_1.h>
#include <dwrite.h>
#include <wincodec.h>
#include <uuids.h>
#include "winrt\Windows.ApplicationModel.h"

// std
#include <string>
#include <format>

// WIL, requires "Microsoft.Windows.ImplementationLibrary" nuget
#include "wil/result.h"
#include "wil/stl.h"
#include "wil/win32_helpers.h"
#include "wil/com.h"

// C++/WinRT, requires "Microsoft.Windows.CppWinRT" nuget
#include "winrt/base.h"

// project globals
#include "wintrace.h"

#pragma comment(lib, "mfsensorgroup")
// {7B1C4D2E-8F3A-4B5C-9D6E-1F2A3B4C5D6E}
extern GUID CLSID_WebcamSwitcherSource;

