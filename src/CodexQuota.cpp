#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <commctrl.h>
#include "PluginInterface.h"
extern "C" { int _fltused = 0; }

// The DLL uses only Win32 so it needs no separately installed C++ runtime.
void* operator new(decltype(sizeof(0)), void* address) noexcept { return address; }
extern "C" void* memset(void* destination, int value, decltype(sizeof(0)) count) {
    auto out = static_cast<volatile unsigned char*>(destination);
    for (decltype(count) i = 0; i < count; ++i) out[i] = static_cast<unsigned char>(value);
    return destination;
}
extern "C" void* memcpy(void* destination, const void* source, decltype(sizeof(0)) count) {
    auto out = static_cast<volatile unsigned char*>(destination);
    auto in = static_cast<const unsigned char*>(source);
    for (decltype(count) i = 0; i < count; ++i) out[i] = in[i];
    return destination;
}

static HMODULE module;
static SRWLOCK stateLock = SRWLOCK_INIT;
static INIT_ONCE init = INIT_ONCE_STATIC_INIT;
static wchar_t directory[2048], config[2048], dataDirectory[2048], quotaPath[2048], activityPath[2048];
static wchar_t values[3][128] = { L"...", L"未连接", L"--" };
static wchar_t tooltip[8192] = L"正在读取 Codex 额度。";
static wchar_t queryError[256];
static HANDLE child, childJob;
static HANDLE monitorChild;
static ULONGLONG monitorAttempt;
static ULONGLONG lastAttempt, childStarted;
static DWORD viewTls;

struct View { View* next; wchar_t value[3][128]; wchar_t tooltip[8192]; };
static View* views;
static HWND tipWindow;
static WNDPROC originalTipProc;
static wchar_t tipText[2][8192], activityText[4096];
static int tipIndex;
static bool tipSized;

static View* CurrentView() {
    auto view = static_cast<View*>(TlsGetValue(viewTls));
    if (!view) {
        view = static_cast<View*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, sizeof(View)));
        if (!view) return nullptr;
        AcquireSRWLockExclusive(&stateLock);
        view->next = views;
        views = view;
        ReleaseSRWLockExclusive(&stateLock);
        TlsSetValue(viewTls, view);
    }
    return view;
}

static void Append(wchar_t* destination, const wchar_t* text) {
    int used = lstrlenW(destination);
    lstrcpynW(destination + used, text, 8192 - used);
}

static unsigned long long Number(const wchar_t* text) {
    unsigned long long number = 0;
    for (; *text >= L'0' && *text <= L'9'; ++text) number = number * 10 + (*text - L'0');
    return number;
}

static LRESULT CALLBACK TooltipProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
    WNDPROC original = originalTipProc;
    if (message == TTM_UPDATETIPTEXTW) {
        AcquireSRWLockShared(&stateLock);
        bool unchanged = !lstrcmpW(tipText[tipIndex], tooltip);
        int next = unchanged ? tipIndex : 1 - tipIndex;
        if (!unchanged) lstrcpynW(tipText[next], tooltip, 8192);
        ReleaseSRWLockShared(&stateLock);
        auto source = reinterpret_cast<TOOLINFOW*>(lParam);
        TOOLINFOW info{};
        memcpy(&info, source, source->cbSize < sizeof(info) ? source->cbSize : sizeof(info));
        info.lpszText = tipText[next];
        if (!tipSized) { CallWindowProcW(original, window, TTM_SETMAXTIPWIDTH, 0, 440); tipSized = true; }
        LRESULT result = CallWindowProcW(original, window, message, wParam, reinterpret_cast<LPARAM>(&info));
        tipIndex = next;
        return result;
    }
    LRESULT result = CallWindowProcW(original, window, message, wParam, lParam);
    if (message == WM_NCDESTROY && window == tipWindow) { tipWindow = nullptr; originalTipProc = nullptr; tipText[0][0] = tipText[1][0] = 0; tipSized = false; }
    return result;
}

static BOOL CALLBACK FindTooltip(HWND window, LPARAM) {
    DWORD process = 0; GetWindowThreadProcessId(window, &process);
    if (process != GetCurrentProcessId()) return TRUE;
    wchar_t name[64]; GetClassNameW(window, name, 64);
    if (lstrcmpW(name, L"tooltips_class32")) return TRUE;
    HWND owner = GetParent(window);
    HWND root = GetAncestor(owner, GA_ROOT);
    GetClassNameW(root, name, 64);
    if (lstrcmpW(name, L"Shell_TrayWnd") && lstrcmpW(name, L"Shell_SecondaryTrayWnd")) return TRUE;
    originalTipProc = reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window, GWLP_WNDPROC));
    if (!originalTipProc) return TRUE;
    if (!SetWindowLongPtrW(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(TooltipProcedure))) { originalTipProc = nullptr; return TRUE; }
    tipWindow = window;
    return FALSE;
}

static void InstallTooltip() { if (!IsWindow(tipWindow)) EnumWindows(FindTooltip, 0); }

static unsigned long long Now() {
    FILETIME value;
    GetSystemTimeAsFileTime(&value);
    ULARGE_INTEGER ticks;
    ticks.LowPart = value.dwLowDateTime; ticks.HighPart = value.dwHighDateTime;
    return (ticks.QuadPart - 116444736000000000ULL) / 10000000;
}

static void Join(wchar_t* out, const wchar_t* base, const wchar_t* name) {
    lstrcpynW(out, base, 2048);
    int length = lstrlenW(out);
    if (length && out[length - 1] != L'\\') { lstrcpynW(out + length, L"\\", 2048 - length); ++length; }
    lstrcpynW(out + length, name, 2048 - length);
}

static bool OwnerAlive(const wchar_t* file) {
    DWORD pid = GetPrivateProfileIntW(L"data", L"owner", 0, file);
    if (!pid) return false;
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) return false;
    FILETIME created, exited, kernel, user;
    DWORD code = 0;
    bool alive = GetExitCodeProcess(process, &code) && code == STILL_ACTIVE && GetProcessTimes(process, &created, &exited, &kernel, &user);
    CloseHandle(process);
    if (!alive) return false;
    wchar_t started[64];
    GetPrivateProfileStringW(L"data", L"owner_started", L"0", started, 64, file);
    ULARGE_INTEGER time; time.LowPart = created.dwLowDateTime; time.HighPart = created.dwHighDateTime;
    return time.QuadPart == Number(started);
}

static void StartMonitor() {
    auto storage = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 12288));
    if (!storage) return;
    wchar_t* executable = storage; wchar_t* command = storage + 2048;
    Join(executable, directory, L"CodexQuotaBridge.exe");
    wsprintfW(command, L"\"%s\" monitor %lu", executable, GetCurrentProcessId());
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (CreateProcessW(executable, command, nullptr, nullptr, FALSE, CREATE_NO_WINDOW, nullptr, directory, &startup, &process)) {
        monitorChild = process.hProcess; CloseHandle(process.hThread);
    }
    HeapFree(GetProcessHeap(), 0, storage);
    monitorAttempt = GetTickCount64();
}

static void Status() {
    wchar_t updated[64];
    GetPrivateProfileStringW(L"data", L"updated", L"0", updated, 64, activityPath);
    if (!OwnerAlive(activityPath) || Now() > Number(updated) + 10) {
        lstrcpynW(values[1], monitorChild ? L"读取中" : L"状态不可用", 128);
        Append(tooltip, L"\n\n任务："); Append(tooltip, values[1]);
        return;
    }
    GetPrivateProfileStringW(L"data", L"label", L"状态不可用", values[1], 128, activityPath);
    GetPrivateProfileStringW(L"data", L"tooltip", L"", activityText, 4096, activityPath);
    for (auto p = activityText; *p; ++p) if (*p == 0x2028) *p = L'\n';
    Append(tooltip, L"\n\n"); Append(tooltip, activityText);
}

static void StartQuota() {
    auto storage = static_cast<wchar_t*>(HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, 20480));
    if (!storage) { lstrcpynW(values[0], L"ERR", 128); return; }
    wchar_t* executable = storage; wchar_t* command = storage + 2048; wchar_t* codex = storage + 8192;
    Join(executable, directory, L"CodexQuotaBridge.exe");
    GetPrivateProfileStringW(L"plugin", L"codex_path", L"", codex, 2048, config);
    wsprintfW(command, L"\"%s\" quota \"%s\" \"%s\"", executable, quotaPath, codex);
    STARTUPINFOW startup{}; startup.cb = sizeof(startup);
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(executable, command, nullptr, nullptr, FALSE, CREATE_NO_WINDOW | CREATE_SUSPENDED, nullptr, directory, &startup, &process)) {
        lstrcpynW(values[0], L"ERR", 128);
        wsprintfW(queryError, L"无法启动 CodexQuotaBridge.exe，Windows 错误 %lu。请将它和 DLL 放在同一目录。", GetLastError());
        lastAttempt = GetTickCount64();
        HeapFree(GetProcessHeap(), 0, storage);
        return;
    }
    HeapFree(GetProcessHeap(), 0, storage);
    childJob = CreateJobObjectW(nullptr, nullptr);
    JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits{};
    limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
    bool assigned = childJob && SetInformationJobObject(childJob, JobObjectExtendedLimitInformation, &limits, sizeof(limits)) && AssignProcessToJobObject(childJob, process.hProcess);
    if (!assigned) {
        TerminateProcess(process.hProcess, 1); CloseHandle(process.hThread); CloseHandle(process.hProcess);
        if (childJob) CloseHandle(childJob); childJob = nullptr;
        lstrcpynW(values[0], L"ERR", 128);
        lstrcpynW(queryError, L"无法隔离额度查询进程，请检查当前进程的 Windows Job 限制。", 256);
        lastAttempt = GetTickCount64();
        return;
    }
    ResumeThread(process.hThread); CloseHandle(process.hThread);
    child = process.hProcess;
    lastAttempt = childStarted = GetTickCount64();
}

class Item final : public IPluginItem {
    int index;
public:
    explicit Item(int value) : index(value) {}
    const wchar_t* GetItemName() const override { return index == 0 ? L"Codex 剩余额度" : index == 1 ? L"Codex 任务状态" : L"Codex 重置倒计时"; }
    const wchar_t* GetItemId() const override { return index == 0 ? L"codex_quota_remaining" : index == 1 ? L"codex_task_status" : L"codex_quota_reset"; }
    const wchar_t* GetItemLableText() const override { return index == 0 ? L"Codex " : index == 1 ? L"" : L"重置 "; }
    const wchar_t* GetItemValueText() const override {
        View* view = CurrentView(); if (!view) return L"ERR";
        AcquireSRWLockShared(&stateLock);
        lstrcpynW(view->value[index], values[index], 128);
        ReleaseSRWLockShared(&stateLock);
        return view->value[index];
    }
    const wchar_t* GetItemValueSampleText() const override { return index == 0 ? L"100%/100%" : index == 1 ? L"批准/执行 99" : L"99天23时"; }
    int OnMouseEvent(MouseEventType type, int, int, void* window, int) override {
        if (type != MT_LCLICKED) return 0;
        View* view = CurrentView(); if (!view) return 0;
        AcquireSRWLockShared(&stateLock); lstrcpynW(view->tooltip, tooltip, 8192); ReleaseSRWLockShared(&stateLock);
        MessageBoxW(static_cast<HWND>(window), view->tooltip, L"Codex 状态与额度", MB_OK | MB_ICONINFORMATION);
        return 1;
    }
};

class Plugin final : public ITMPlugin {
    Item items[3] { Item(0), Item(1), Item(2) };
public:
    IPluginItem* GetItem(int index) override { return index >= 0 && index < 3 ? &items[index] : nullptr; }
    const wchar_t* GetInfo(PluginInfoIndex index) override {
        switch (index) {
        case TMI_NAME: return L"Codex 状态与额度";
        case TMI_DESCRIPTION: return L"ChatGPT 订阅额度、重置时间与次数，以及 Codex 生命周期状态";
        case TMI_AUTHOR: return L"Local Codex Quota";
        case TMI_VERSION: return L"0.1.1";
        default: return L"";
        }
    }
    const wchar_t* GetTooltipInfo() override {
        View* view = CurrentView(); if (!view) return L"内存不足";
        AcquireSRWLockShared(&stateLock); lstrcpynW(view->tooltip, tooltip, 8192); ReleaseSRWLockShared(&stateLock);
        return view->tooltip;
    }
    void DataRequired() override {
        AcquireSRWLockExclusive(&stateLock);
        if (monitorChild && WaitForSingleObject(monitorChild, 0) == WAIT_OBJECT_0) { CloseHandle(monitorChild); monitorChild = nullptr; }
        if (!monitorChild && (!monitorAttempt || GetTickCount64() - monitorAttempt >= 30000)) StartMonitor();
        if (child && (WaitForSingleObject(child, 0) == WAIT_OBJECT_0 || GetTickCount64() - childStarted > 60000)) {
            bool timedOut = WaitForSingleObject(child, 0) != WAIT_OBJECT_0;
            DWORD code = 0;
            if (!timedOut) GetExitCodeProcess(child, &code);
            CloseHandle(childJob); childJob = nullptr;
            CloseHandle(child); child = nullptr;
            if (timedOut) {
                lstrcpynW(values[0], L"ERR", 128);
                lstrcpynW(queryError, L"额度查询超过 60 秒，已结束本次查询。", 256);
            }
            else if (code) wsprintfW(queryError, L"上一次额度查询失败，退出码 %lu。", code);
            else queryError[0] = 0;
        }
        if (GetFileAttributesW(quotaPath) != INVALID_FILE_ATTRIBUTES) {
            GetPrivateProfileStringW(L"data", L"remaining", L"--", values[0], 128, quotaPath);
            GetPrivateProfileStringW(L"data", L"tooltip", L"额度信息尚未返回。", tooltip, 8192, quotaPath);
            for (auto p = tooltip; *p; ++p) if (*p == 0x2028) *p = L'\n';
            wchar_t reset[64], updated[64];
            GetPrivateProfileStringW(L"data", L"reset_at", L"0", reset, 64, quotaPath);
            GetPrivateProfileStringW(L"data", L"updated", L"0", updated, 64, quotaPath);
            auto at = Number(reset), now = Now();
            if (!at) lstrcpynW(values[2], L"--", 128);
            else if (at <= now) lstrcpynW(values[2], L"待同步", 128);
            else {
                DWORD left = static_cast<DWORD>(at - now);
                if (left >= 86400) wsprintfW(values[2], L"%lu天%lu时", left / 86400, (left % 86400) / 3600);
                else if (left >= 3600) wsprintfW(values[2], L"%lu时%lu分", left / 3600, (left % 3600) / 60);
                else wsprintfW(values[2], L"%lu分%lu秒", left / 60, left % 60);
            }
            if (now > Number(updated) + 300 || queryError[0]) {
                int length = lstrlenW(values[0]); if (length < 126) { values[0][length] = L'*'; values[0][length + 1] = 0; }
                Append(tooltip, L"\n* 上次查询失败或数据超过 5 分钟未更新。 ");
            }
            if (at && at <= now && !child && GetTickCount64() - lastAttempt >= 10000) lastAttempt = 0;
        }
        else lstrcpynW(tooltip, L"额度信息尚未返回。", 8192);
        Status();
        if (!child && (!lastAttempt || GetTickCount64() - lastAttempt >= 120000)) StartQuota();
        if (queryError[0]) { Append(tooltip, L"\n"); Append(tooltip, queryError); }
        ReleaseSRWLockExclusive(&stateLock);
        InstallTooltip();
    }
    int GetCommandCount() override { return 1; }
    const wchar_t* GetCommandName(int index) override { return index == 0 ? L"刷新 Codex 额度" : nullptr; }
    void OnPluginCommand(int index, void*, void*) override { if (index == 0) { AcquireSRWLockExclusive(&stateLock); lastAttempt = 0; ReleaseSRWLockExclusive(&stateLock); } }
};

alignas(Plugin) static unsigned char pluginStorage[sizeof(Plugin)];
static BOOL CALLBACK Initialize(PINIT_ONCE, PVOID, PVOID*) {
    GetModuleFileNameW(module, directory, 2048);
    HMODULE pinned; GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN, directory, &pinned);
    for (int i = lstrlenW(directory) - 1; i >= 0; --i) if (directory[i] == L'\\') { directory[i] = 0; break; }
    Join(config, directory, L"CodexQuota.ini");
    if (!GetEnvironmentVariableW(L"CODEX_QUOTA_DATA_DIR", dataDirectory, 2048)) {
        GetEnvironmentVariableW(L"LOCALAPPDATA", dataDirectory, 2048);
        int offset = lstrlenW(dataDirectory);
        lstrcpynW(dataDirectory + offset, L"\\CodexQuota", 2048 - offset);
    }
    Join(quotaPath, dataDirectory, L"quota.ini");
    Join(activityPath, dataDirectory, L"activity.ini");
    viewTls = TlsAlloc();
    if (viewTls == TLS_OUT_OF_INDEXES) return FALSE;
    new(pluginStorage) Plugin();
    return TRUE;
}

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() {
    if (!InitOnceExecuteOnce(&init, Initialize, nullptr, nullptr)) return nullptr;
    return reinterpret_cast<Plugin*>(pluginStorage);
}

extern "C" BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) module = instance;
    if (reason == DLL_PROCESS_DETACH) {
        if (monitorChild) { TerminateProcess(monitorChild, 0); CloseHandle(monitorChild); }
        if (childJob) CloseHandle(childJob);
        if (child) CloseHandle(child);
        while (views) { View* next = views->next; HeapFree(GetProcessHeap(), 0, views); views = next; }
    }
    return TRUE;
}
