#include "pch.h"
#include "SourceFormat.h"
#include "WebcamSwitcherProtocol.h"

#include <string>
#include <fstream>

namespace
{
    bool TryReadU32(const std::string& value, uint32_t& out)
    {
        if (value.empty())
            return false;
        try
        {
            size_t pos = 0;
            unsigned long v = std::stoul(value, &pos);
            if (pos != value.size())
                return false;
            out = static_cast<uint32_t>(v);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    void Trim(std::string& s)
    {
        const char* ws = " \t\r\n";
        size_t a = s.find_first_not_of(ws);
        if (a == std::string::npos) { s.clear(); return; }
        size_t b = s.find_last_not_of(ws);
        s = s.substr(a, b - a + 1);
    }
}

WsSourceFormat ReadSourceFormat()
{
    WsSourceFormat fmt;
    fmt.width = WS_DEFAULT_WIDTH;
    fmt.height = WS_DEFAULT_HEIGHT;
    fmt.fpsNum = WS_DEFAULT_FPS_NUM;
    fmt.fpsDen = WS_DEFAULT_FPS_DEN;

    wchar_t programData[MAX_PATH] = {};
    DWORD len = GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH);
    std::wstring path = (len == 0 || len >= MAX_PATH) ? L"C:\\ProgramData" : programData;
    path += L"\\WebcamSwitcher\\format.ini";

    std::ifstream file(path);
    if (!file.is_open())
    {
        WINTRACE(L"ReadSourceFormat: no format file, using %ux%u@%u/%u", fmt.width, fmt.height, fmt.fpsNum, fmt.fpsDen);
        return fmt;
    }

    uint32_t width = 0, height = 0, fps = 0;
    std::string line;
    while (std::getline(file, line))
    {
        size_t eq = line.find('=');
        if (eq == std::string::npos)
            continue;
        std::string key = line.substr(0, eq);
        std::string val = line.substr(eq + 1);
        Trim(key);
        Trim(val);

        if (key == "width")      TryReadU32(val, width);
        else if (key == "height") TryReadU32(val, height);
        else if (key == "fps")    TryReadU32(val, fps);
    }

    // Validate + round to even where required (NV12).
    if (width >= 160 && width <= 3840)
        fmt.width = width & ~1u;
    if (height >= 160 && height <= 3840)
        fmt.height = height & ~1u;
    if (fps >= 1 && fps <= 60)
    {
        fmt.fpsNum = fps;
        fmt.fpsDen = 1;
    }

    WINTRACE(L"ReadSourceFormat: %ux%u @ %u/%u fps", fmt.width, fmt.height, fmt.fpsNum, fmt.fpsDen);
    return fmt;
}
