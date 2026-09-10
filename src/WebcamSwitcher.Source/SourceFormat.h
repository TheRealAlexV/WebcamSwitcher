#pragma once
#include <cstdint>

// Output format read from %ProgramData%\WebcamSwitcher\format.ini at
// media-source construction. Falls back to the protocol defaults on error.
struct WsSourceFormat
{
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t fpsNum = 0;
    uint32_t fpsDen = 1;
};

WsSourceFormat ReadSourceFormat();
