// SOD Performance - native stack sampler.
//
// A plain Win32 thread (unknown to the .NET runtime and to the IL2CPP GC) that, about 1000 times per second,
// suspends the game's main thread, reads its registers, walks its stack using the x64 unwind tables (.pdata) of
// the modules we were given (GameAssembly.dll, UnityPlayer.dll, system DLLs), resumes it, and stores the list of
// function start addresses in a ring buffer that the plugin drains and turns into "where the time goes".
//
// Safety rules (the main thread is frozen while we work):
//  - no heap allocation, no locks, no loader calls between SuspendThread and ResumeThread;
//  - our own lookup in each module's .pdata (no RtlLookupFunctionEntry, which can take locks);
//  - only walk frames whose code lies inside a known image; stop at anything else (JIT code, trampolines);
//  - every stack read is checked against the thread's stack bounds read from its TEB.
#include <windows.h>
#include <stdint.h>

#define MAXD 64
#define RING 8192
#define MAXMOD 96

typedef struct {
    int32_t tag;
    int32_t frame;
    int32_t n;
    int32_t pad;
    uint64_t fn[MAXD];
} Sample;

typedef struct {
    uint64_t base, end;
    const RUNTIME_FUNCTION* rf;
    uint32_t count;
} Module;

typedef LONG (NTAPI *NtQueryInformationThread_t)(HANDLE, int, PVOID, ULONG, PULONG);
typedef struct {
    LONG ExitStatus;
    PVOID TebBaseAddress;
    PVOID UniqueProcess;
    PVOID UniqueThread;
    ULONG_PTR AffinityMask;
    LONG Priority;
    LONG BasePriority;
} THREAD_BASIC_INFORMATION_X;

static Module g_mods[MAXMOD];
static int g_nmods;
static HANDLE g_target;
static HANDLE g_thread;
static volatile LONG g_run;
static volatile LONG g_tag;
static volatile LONG g_frame;
static volatile LONG g_intervalUs = 1000;
static Sample* g_ring;
static volatile LONG64 g_head;
static LONG64 g_tail;
static volatile LONG64 g_taken, g_failed, g_maxSuspendTicks, g_overruns;
static uint8_t* g_teb;
static CONTEXT* g_ctx;

static const Module* find_module(uint64_t pc)
{
    for (int i = 0; i < g_nmods; i++)
        if (pc >= g_mods[i].base && pc < g_mods[i].end) return &g_mods[i];
    return NULL;
}

static const RUNTIME_FUNCTION* find_rf(const Module* m, uint64_t pc)
{
    if (!m->rf || m->count == 0) return NULL;
    uint32_t rva = (uint32_t)(pc - m->base);
    uint32_t lo = 0, hi = m->count;
    while (lo < hi) {
        uint32_t mid = lo + (hi - lo) / 2;
        const RUNTIME_FUNCTION* r = &m->rf[mid];
        if (rva < r->BeginAddress) hi = mid;
        else if (rva >= r->EndAddress) lo = mid + 1;
        else return r;
    }
    return NULL;
}

// Follow chained unwind info back to the primary entry so a function split into parts gets one start address.
static uint32_t primary_begin(const Module* m, const RUNTIME_FUNCTION* r)
{
    for (int guard = 0; guard < 8; guard++) {
        uint32_t ui = r->UnwindData;
        if (ui & 1) break;   // indirect entry, leave as is
        const uint8_t* info = (const uint8_t*)(m->base + ui);
        uint8_t flags = info[0] >> 3;
        if (!(flags & 0x4)) break;   // UNW_FLAG_CHAININFO
        uint8_t count = info[2];
        uint32_t off = 4 + ((count + 1) & ~1) * 2;
        r = (const RUNTIME_FUNCTION*)(info + off);
    }
    return r->BeginAddress;
}

static int init_module(Module* m, uint64_t base)
{
    const IMAGE_DOS_HEADER* dos = (const IMAGE_DOS_HEADER*)base;
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return 0;
    const IMAGE_NT_HEADERS64* nt = (const IMAGE_NT_HEADERS64*)(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return 0;
    m->base = base;
    m->end = base + nt->OptionalHeader.SizeOfImage;
    const IMAGE_DATA_DIRECTORY* d = &nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXCEPTION];
    if (d->VirtualAddress && d->Size >= sizeof(RUNTIME_FUNCTION)) {
        m->rf = (const RUNTIME_FUNCTION*)(base + d->VirtualAddress);
        m->count = d->Size / sizeof(RUNTIME_FUNCTION);
    }
    return 1;
}

static void take_sample(void)
{
    LARGE_INTEGER t0, t1;
    QueryPerformanceCounter(&t0);
    if (SuspendThread(g_target) == (DWORD)-1) { g_failed++; return; }

    Sample* s = &g_ring[g_head % RING];
    s->tag = g_tag;
    s->frame = g_frame;
    int n = 0;
    g_ctx->ContextFlags = CONTEXT_FULL;
    if (GetThreadContext(g_target, g_ctx)) {
        uint64_t stackBase = *(uint64_t*)(g_teb + 0x8);
        uint64_t stackLimit = *(uint64_t*)(g_teb + 0x10);
        for (; n < MAXD; ) {
            uint64_t pc = g_ctx->Rip;
            uint64_t sp = g_ctx->Rsp;
            if (pc == 0 || sp < stackLimit || sp >= stackBase) break;
            const Module* m = find_module(pc);
            if (!m) { s->fn[n++] = pc; break; }   // code outside known images (JIT, trampolines): record and stop
            const RUNTIME_FUNCTION* r = find_rf(m, pc);
            if (!r) {
                // leaf function without unwind data: return address is at [rsp]
                s->fn[n++] = pc;
                if (sp + 8 > stackBase) break;
                g_ctx->Rip = *(uint64_t*)sp;
                g_ctx->Rsp = sp + 8;
            } else {
                s->fn[n++] = m->base + primary_begin(m, r);
                // Unwinding reads this frame's saved registers and return address above sp. The top few KB of the stack
                // only hold the thread start frames; stopping there keeps every read well inside the stack.
                if (sp + 2048 >= stackBase) break;
                PVOID handlerData;
                DWORD64 establisher;
                RtlVirtualUnwind(UNW_FLAG_NHANDLER, m->base, pc, (PRUNTIME_FUNCTION)r, g_ctx, &handlerData, &establisher, NULL);
            }
            if (g_ctx->Rsp <= sp) break;   // must move up the stack
        }
    }
    ResumeThread(g_target);

    QueryPerformanceCounter(&t1);
    LONG64 dt = t1.QuadPart - t0.QuadPart;
    if (dt > g_maxSuspendTicks) g_maxSuspendTicks = dt;
    s->n = n;
    MemoryBarrier();
    g_head++;
    g_taken++;
}

static DWORD WINAPI sampler_main(LPVOID unused)
{
    (void)unused;
    HANDLE timer = CreateWaitableTimerExW(NULL, NULL, 0x00000002 /*HIGH_RESOLUTION*/, TIMER_ALL_ACCESS);
    SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST);
    while (g_run) {
        LONG us = g_intervalUs;
        if (us < 250) us = 250;
        if (timer) {
            LARGE_INTEGER due;
            due.QuadPart = -(LONGLONG)us * 10;
            SetWaitableTimer(timer, &due, 0, NULL, NULL, FALSE);
            WaitForSingleObject(timer, 50);
        } else {
            Sleep(1);
        }
        if (!g_run) break;
        LONG kind = g_tag & 0xff;
        if (g_tag < 0 || kind == 2 || kind == 3) continue;   // menu/paused or fast-forward: nothing to measure
        take_sample();
    }
    if (timer) CloseHandle(timer);
    return 0;
}

__declspec(dllexport) int sod_start(uint32_t threadId, const uint64_t* bases, int nbases, int intervalUs)
{
    if (g_thread) return 1;
    g_nmods = 0;
    for (int i = 0; i < nbases && g_nmods < MAXMOD; i++)
        if (init_module(&g_mods[g_nmods], bases[i])) g_nmods++;
    if (g_nmods == 0) return -1;

    g_target = OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION, FALSE, threadId);
    if (!g_target) return -2;

    NtQueryInformationThread_t q = (NtQueryInformationThread_t)GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQueryInformationThread");
    if (!q) return -3;
    THREAD_BASIC_INFORMATION_X tbi;
    if (q(g_target, 0, &tbi, sizeof(tbi), NULL) != 0 || !tbi.TebBaseAddress) return -4;
    g_teb = (uint8_t*)tbi.TebBaseAddress;

    g_ring = (Sample*)VirtualAlloc(NULL, sizeof(Sample) * RING, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    g_ctx = (CONTEXT*)VirtualAlloc(NULL, sizeof(CONTEXT) + 64, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);   // page aligned
    if (!g_ring || !g_ctx) return -5;

    g_intervalUs = intervalUs > 0 ? intervalUs : 1000;
    g_run = 1;
    g_thread = CreateThread(NULL, 64 * 1024, sampler_main, NULL, 0, NULL);
    return g_thread ? 0 : -6;
}

__declspec(dllexport) void sod_stop(void)
{
    g_run = 0;
}

__declspec(dllexport) void sod_set_tag(int tag, int frame)
{
    g_tag = tag;
    g_frame = frame;
}

// Copies up to max new samples into out; returns the number copied. Single consumer.
__declspec(dllexport) int sod_read(Sample* out, int max)
{
    LONG64 head = g_head;
    MemoryBarrier();
    if (head - g_tail > RING) {
        g_overruns += (head - g_tail) - RING;
        g_tail = head - RING;
    }
    int c = 0;
    while (g_tail < head && c < max) {
        out[c++] = g_ring[g_tail % RING];
        g_tail++;
    }
    return c;
}

__declspec(dllexport) void sod_stats(int64_t* out)
{
    LARGE_INTEGER f;
    QueryPerformanceFrequency(&f);
    out[0] = g_taken;
    out[1] = g_failed;
    out[2] = g_maxSuspendTicks * 1000000 / f.QuadPart;
    out[3] = g_overruns;
    out[4] = g_nmods;
}

// ---------------------------------------------------------------------------------------------------------------
// Fast SoCustomComparison.Equals.
// The game's version returns true when both are non-null, of the same type, and have the same preset name (ordinal). It calls GetType() four times per comparison, and each GetType() goes through IL2CPP's reflection type
// cache under a lock. Loading compares presets millions of times (EvidenceCreator.CreateFact searches lists with it),
// which was ~55 s of a 138 s load. Same result here: exact type = same class pointer; names compared as strings; the
// name is read from the cached presetName field and filled in by the game's own GetPresetName when still empty.
// The game's function is replaced by a jump to this one at start-up (nothing calls the old body afterwards).

typedef struct { void* klass; void* monitor; int32_t length; uint16_t chars[1]; } Il2CppStr;
typedef Il2CppStr* (*GetPresetName_t)(void* self, void* method);
static GetPresetName_t g_getName;
static void* g_getNameMethod;
static int g_nameOff = 0x18;   // SoCustomComparison.presetName, passed in from the game's metadata
static volatile LONG64 g_eqCalls;

static uint8_t fast_preset_equals(void* self, void* other, void* method)
{
    (void)method;
    g_eqCalls++;
    if (!self || !other) return 0;
    if (*(void**)self != *(void**)other) return 0;   // GetType() differs
    if (self == other) return 1;
    Il2CppStr* a = *(Il2CppStr**)((uint8_t*)other + g_nameOff);
    if (!a || a->length < 1) a = g_getName(other, g_getNameMethod);
    Il2CppStr* b = *(Il2CppStr**)((uint8_t*)self + g_nameOff);
    if (!b || b->length < 1) b = g_getName(self, g_getNameMethod);
    if (a == b) return 1;
    if (!a || !b || a->length != b->length) return 0;
    const uint16_t* x = a->chars;
    const uint16_t* y = b->chars;
    for (int32_t i = 0; i < a->length; i++) if (x[i] != y[i]) return 0;
    return 1;
}

static uint8_t g_saved[16];
static void* g_patched;

// Returns 0 on success.
__declspec(dllexport) int sod_patch_preset_equals(void* target, void* getName, void* getNameMethod, int nameOffset)
{
    if (!target || !getName || nameOffset <= 0 || nameOffset > 4096) return -1;
    if (g_patched) return 1;
    g_nameOff = nameOffset;
    g_getName = (GetPresetName_t)getName;
    g_getNameMethod = getNameMethod;
    uint8_t jmp[12] = { 0x48, 0xB8, 0, 0, 0, 0, 0, 0, 0, 0, 0xFF, 0xE0 };   // mov rax, imm64 ; jmp rax
    uint64_t dst = (uint64_t)(uintptr_t)&fast_preset_equals;
    for (int i = 0; i < 8; i++) jmp[2 + i] = (uint8_t)(dst >> (8 * i));
    DWORD old;
    if (!VirtualProtect(target, sizeof(jmp), PAGE_EXECUTE_READWRITE, &old)) return -2;
    for (int i = 0; i < 12; i++) g_saved[i] = ((uint8_t*)target)[i];
    for (int i = 0; i < 12; i++) ((volatile uint8_t*)target)[i] = jmp[i];
    VirtualProtect(target, sizeof(jmp), old, &old);
    FlushInstructionCache(GetCurrentProcess(), target, sizeof(jmp));
    g_patched = target;
    return 0;
}

// Puts the game's original bytes back (used when the setting is turned off at runtime is not supported; kept for safety).
__declspec(dllexport) int sod_unpatch_preset_equals(void)
{
    if (!g_patched) return 1;
    DWORD old;
    if (!VirtualProtect(g_patched, 12, PAGE_EXECUTE_READWRITE, &old)) return -2;
    for (int i = 0; i < 12; i++) ((volatile uint8_t*)g_patched)[i] = g_saved[i];
    VirtualProtect(g_patched, 12, old, &old);
    FlushInstructionCache(GetCurrentProcess(), g_patched, 12);
    g_patched = NULL;
    return 0;
}

// 1 if addr is exactly the start of a function in its module's unwind table (.pdata), else 0.
__declspec(dllexport) int sod_is_function_start(void* addr)
{
    HMODULE h = NULL;
    if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)addr, &h) || !h) return 0;
    Module m;
    m.base = 0; m.end = 0; m.rf = NULL; m.count = 0;
    if (!init_module(&m, (uint64_t)(uintptr_t)h)) return 0;
    const RUNTIME_FUNCTION* r = find_rf(&m, (uint64_t)(uintptr_t)addr);
    if (!r) return 0;
    return m.base + r->BeginAddress == (uint64_t)(uintptr_t)addr ? 1 : 0;
}

__declspec(dllexport) int64_t sod_preset_equals_calls(void)
{
    return g_eqCalls;
}

__declspec(dllexport) int sod_sample_size(void)
{
    return (int)sizeof(Sample);
}
