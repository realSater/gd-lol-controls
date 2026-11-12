// GDLoLHook.dll - loaded into Grim Dawn by GDLoLControls.exe while the app runs.
//
// It resolves the game's exported functions by name (no hard-coded addresses) and hooks the game's once-per-frame call to Engine::Update
// through the import table of Grim Dawn.exe (no code is patched). After each game update, on the game thread, it:
//  - reads the player and the characters nearby (position, health, friend or foe) into shared memory, and signals a per-frame event;
//  - runs the commands the app queued: the same actions a player issues with the mouse and keyboard (attack a target, walk to a point,
//    use a hotbar slot, drink a potion).
// It never changes stats, items, experience, damage or save files.
//
// Rules: no work in DllMain; everything that touches the game runs inside the game's own update call, guarded by SEH; if a required export
// is missing nothing is installed; unloading restores the import table and waits for any call in flight.#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stdint.h>
#include <math.h>

#define SHM_VERSION 4

#pragma pack(push, 8)
struct Shared
{
    uint32_t magic;            // 'GDLH'
    uint32_t version;
    volatile LONG seq;         // odd while the DLL writes
    uint32_t status;           // bit 0 loaded, 1 exports ok, 2 hook installed, 3 in game (player found), 4 unloading
    uint32_t frame;            // game updates seen
    uint32_t faults;           // SEH faults caught in the per-frame work
    uint32_t resolved;         // how many exports were found
    uint32_t wanted;
    char missing[512];         // names of the exports not found
    uint64_t gameEngine, engine, player, camera;
    double life;
    float lifeLimit;
    int32_t lifeState;
    uint8_t coords[128];       // raw WorldCoords of the player: Region*, then the position (region-relative)
    uint8_t playerClass[64];   // unused (kept for the layout)
    uint8_t characterClass[64];
    uint64_t playerClassPtr, characterClassPtr, monsterClassPtr;
    volatile LONG quit;        // set by the app: unload
    char note[256];
    // ---- characters around the player
    uint32_t entityCount;      // how many entries below are filled
    uint32_t seen;             // entities in view before filtering
    float playerWorld[3];      // world position of the player (world units)
    float playerScreen[2];     // where the player's feet are on screen (as WorldCamera::Project returns it)
    uint8_t viewport[32];      // raw Viewport: x, y, width, height (int)
    int32_t rttiSlot, targetSlot;   // vtable slots found for GetRTTIClassInfo / IsTargetable (-1 = not found)
    uint32_t classChainOk;     // 0x100 | offset of the parent link in RTTI_ClassInfo, 0 when not found
    uint32_t pad2;
    struct Ent
    {
        uint64_t ptr;
        uint32_t id;
        uint32_t flags;        // 1 character, 2 attackable class, 4 friend of the player, 8 targetable, 16 alive (life > 0), 32 monster
        float life, lifeLimit;
        float world[3];        // world position (world units)
        float screen[2];       // feet on screen
        float screenBody[2];   // a little above the feet (where a click hits the body)
        float distance;        // from the player, world units
        float pad;
    } ents[128];
    // ---- a single attack command (kept for older app versions; the queue below is used now)
    volatile LONG cmdSeq;      // the app increments it after filling the fields below
    uint32_t cmdTarget;        // object id of the monster to attack
    uint32_t cmdKind;          // 1 = attack (the game's own mouse action on that target)
    volatile LONG doneSeq;     // the helper sets it to cmdSeq once done
    int32_t doneResult;        // 1 ok, 0 the game refused, -1 no player, -2 no controller, -3 target gone, -4 not a living character, -5 fault
    uint32_t combatEnemy;      // what the game's controller has as its combat enemy now
    // ---- more of the game's state, every frame
    float mana, manaLimit;
    uint32_t flags2;           // 1 in game, 2 alive, 4 game time paused, 8 moving, 16 move command, 32 button held (command repeated)
    uint32_t hoverEnemy, hoverAlly;      // the controller's combat enemy / ally (what the mouse points at, between our commands)
    float cursorGround[3];     // world position of the ground under the mouse cursor (absolute)
    uint32_t cursorGroundOk;
    float targetPoint[3];      // ControllerPlayer::GetTargetPoint (absolute world)
    float repeatPoint[3];      // ControllerPlayer::GetMouseRepeatData point (absolute world): where the last click landed
    uint32_t repeatId;         // ... and what it was on (0 = ground)
    int32_t hpPotionStatus, mpPotionStatus;       // PlayerHotSlotCtrl::Get*PotionStatus
    int32_t hpPotionCooldown, mpPotionCooldown;   // ms left (-1 unknown)
    uint32_t slotSkill[12];    // skill id on each hotbar slot (0 = none)
    float ringRadius;          // written by the app: radius (world units) of the range ring to project
    uint32_t ringCount;
    float ring[48][2];         // the ring on screen (client pixels)
    // command queue: the app fills q[qWrite % 16] and then increments qWrite; the helper runs everything up to qWrite each frame
    volatile LONG qWrite, qRead;
    struct Cmd
    {
        uint32_t kind;         // 1 attack(a=target) 2 move to world(x,y,z) 3 move to screen point(x,y) 4 drink health 5 drink energy 6 cast slot a at target b
        uint32_t a, b;
        float x, y, z;
        int32_t result;        // written back: 1 done, 0 refused, negative = could not (see Run())
    } q[16];
};
#pragma pack(pop)
// the app (C#) reads these offsets: keep them in step with GameLink.cs
#include <stddef.h>
static_assert(offsetof(Shared, missing) == 32, "layout");
static_assert(offsetof(Shared, gameEngine) == 544, "layout");
static_assert(offsetof(Shared, life) == 576, "layout");
static_assert(offsetof(Shared, lifeLimit) == 584, "layout");
static_assert(offsetof(Shared, coords) == 592, "layout");
static_assert(offsetof(Shared, characterClass) == 784, "layout");
static_assert(offsetof(Shared, playerClassPtr) == 848, "layout");
static_assert(offsetof(Shared, quit) == 872, "layout");
static_assert(offsetof(Shared, note) == 876, "layout");
static_assert(offsetof(Shared, entityCount) == 1132, "layout");
static_assert(offsetof(Shared, playerWorld) == 1140, "layout");
static_assert(offsetof(Shared, playerScreen) == 1152, "layout");
static_assert(offsetof(Shared, viewport) == 1160, "layout");
static_assert(offsetof(Shared, rttiSlot) == 1192, "layout");
static_assert(offsetof(Shared, classChainOk) == 1200, "layout");
static_assert(offsetof(Shared, ents) == 1208, "layout");
static_assert(sizeof(Shared::Ent) == 64, "layout");
static_assert(offsetof(Shared, cmdSeq) == 9400, "layout");
static_assert(offsetof(Shared, doneSeq) == 9412, "layout");
static_assert(offsetof(Shared, combatEnemy) == 9420, "layout");
static_assert(offsetof(Shared, mana) == 9424, "layout");
static_assert(offsetof(Shared, flags2) == 9432, "layout");
static_assert(offsetof(Shared, cursorGround) == 9444, "layout");
static_assert(offsetof(Shared, targetPoint) == 9460, "layout");
static_assert(offsetof(Shared, repeatPoint) == 9472, "layout");
static_assert(offsetof(Shared, repeatId) == 9484, "layout");
static_assert(offsetof(Shared, hpPotionStatus) == 9488, "layout");
static_assert(offsetof(Shared, slotSkill) == 9504, "layout");
static_assert(offsetof(Shared, ringRadius) == 9552, "layout");
static_assert(offsetof(Shared, ring) == 9560, "layout");
static_assert(offsetof(Shared, qWrite) == 9944, "layout");
static_assert(offsetof(Shared, q) == 9952, "layout");
static_assert(sizeof(Shared::Cmd) == 28, "layout");
static_assert(sizeof(Shared) == 10400, "layout");

typedef void(__fastcall* EngineUpdateFn)(void* self, const void* sphere, const void* frustum, bool a, bool b);
typedef void*(__fastcall* GetPtrFn)(const void* self);
typedef double(__fastcall* GetDoubleFn)(const void* self);
typedef float(__fastcall* GetFloatFn)(const void* self);
typedef int(__fastcall* GetIntFn)(const void* self);
typedef void*(__fastcall* GetByValFn)(const void* self, void* ret);          // MSVC x64: a class returned by value comes back through a hidden pointer
typedef const void*(__fastcall* StaticClassFn)();

static HMODULE gSelf;
static HANDLE gMap, gFrameEvent, gQuitEvent;
static Shared* gShm;
static volatile LONG gInHook;
static EngineUpdateFn gOrigUpdate;
static void** gIatSlot;

static void** pGameEngine;      // ?gGameEngine@GAME@@3PEAVGameEngine@1@EA (a global pointer)
static void** pEngine;          // ?gEngine@GAME@@3PEAVEngine@1@EA
static GetPtrFn fGetMainPlayer, fGetCamera;
static GetDoubleFn fGetCurrentLife;
static GetFloatFn fGetLifeLimit;
static GetIntFn fGetLifeState;
static GetByValFn fGetCoords;
static GetPtrFn fGetRttiCharacter;   // virtual GetRTTIClassInfo, called through the object's own vtable instead
static StaticClassFn fStaticCharacter, fStaticMonster;

// characters around the player
typedef bool(__fastcall* GetEntsFn)(void* engine, void* vec);
typedef bool(__fastcall* IsAttackableFn)(const void* gameEngine, void* entity);
typedef bool(__fastcall* IsFriendFn)(void* factionMgr, const void* a, const void* b);
typedef void*(__fastcall* WorldVec3CtorFn)(void* self, void* region, const void* vec3);
typedef void*(__fastcall* GetWorldPosFn)(const void* wv3, void* ret);                 // Vec3 by value
typedef void*(__fastcall* ProjectFn)(const void* cam, void* ret, const void* wv3, const void* viewport);  // Vec2 by value
typedef const void*(__fastcall* GetViewportFn)(const void* canvas);
typedef void*(__fastcall* GetCanvasFn)(void* graphicsEngine);
typedef uint32_t(__fastcall* GetIdFn)(const void* obj);
typedef bool(__fastcall* BoolFn)(const void* obj);
static GetEntsFn fGetEnts;
static IsAttackableFn fIsAttackable;
static GetPtrFn fGetFactionMgr, fGetGraphicsEngine;
static IsFriendFn fIsFriend;
static WorldVec3CtorFn fWorldVec3;
static GetWorldPosFn fGetWorldPos;
static ProjectFn fProject;
static GetCanvasFn fGetCanvas;
static GetViewportFn fGetViewport;
static GetIdFn fGetObjectId;
static void* aPlayerRtti;        // Player::GetRTTIClassInfo (to find the virtual slot)
static void* aCharTargetable;    // Character::IsTargetable (to find the virtual slot)
static int gRttiSlot = -2, gTargetSlot = -2;
static int gParentOff = -2;      // where RTTI_ClassInfo keeps its parent class (found at run time)
typedef void(__fastcall* SphereQueryFn)(const void* world, void* vec, void* region, const void* sphere, bool b, int listType);
static SphereQueryFn fSphere;
static const int kWorldOffset = 0x50;   // Engine -> World (Engine::GetEntitiesInPriorFrameFrustum, Engine.dll 9f58348c...)
static const float kRadius = 40.0f;

// commands
typedef void*(__fastcall* GetObjMgrFn)();
typedef void(__fastcall* CsEnterFn)(void* cs, const char* who);
typedef void(__fastcall* CsExitFn)(void* cs);
typedef uint32_t(__fastcall* GetUIntFn)(const void* self);
typedef void(__fastcall* SetUIntFn)(void* self, uint32_t v);
typedef void(__fastcall* SetBoolFn)(void* self, bool v);
typedef bool(__fastcall* HandleMouseFn)(void* ctrl, bool forced, bool primary, bool standStill, bool ally, const void* point, uint32_t* target, bool* flag);
static GetObjMgrFn fObjMgr;
static CsEnterFn fCsEnter;
static CsExitFn fCsExit;
static GetUIntFn fGetControllerId, fGetCombatEnemy;
static SetUIntFn fSetCombatEnemy;
static SetBoolFn fSetNewMouseCommand;
static HandleMouseFn fHandleMouse;
static const void* aControllerPlayerClass;   // ?classInfo@ControllerPlayer@GAME@@1VRTTI_ClassInfo@2@B (data)
static LONG gLastCmd;

// this frame's camera, viewport and player position (set by ReadEntities, used by the version-4 work after it)
static struct { bool ok; const void* cam; const void* vp; void* region; float pos[3]; } gFrame;

// the game's own vector type (same layout as MSVC std::vector): reused every frame, freed once at unload with the game's CRT
struct PtrVec { void** b; void** e; void** c; };
static PtrVec gEnts;

static const char* kUpdateName = "?Update@Engine@GAME@@QEAAXPEBVSphere@2@PEBVWorldFrustum@2@_N1@Z";

static void Note(const char* s)
{
    if (!gShm) return;
    lstrcpynA(gShm->note, s, sizeof(gShm->note));
}

static void AddMissing(const char* name)
{
    if (!gShm) return;
    size_t len = lstrlenA(gShm->missing);
    if (len + lstrlenA(name) + 2 >= sizeof(gShm->missing)) return;
    if (len) { gShm->missing[len++] = ' '; gShm->missing[len] = 0; }
    lstrcatA(gShm->missing, name);
}

template <class T> static bool Resolve(HMODULE m, const char* name, T& out)
{
    gShm->wanted++;
    FARPROC p = m ? GetProcAddress(m, name) : NULL;
    out = (T)p;
    if (p) { gShm->resolved++; return true; }
    AddMissing(name);
    return false;
}

// ---------- per-frame work (game thread) ----------

static void Copy(uint8_t* dst, const void* src, size_t n)
{
    __try { memcpy(dst, src, n); }
    __except (EXCEPTION_EXECUTE_HANDLER) { memset(dst, 0, n); }
}

static bool Readable(const void* p) { return p && (uintptr_t)p > 0x10000 && (uintptr_t)p < 0x7FFFFFFFFFFF; }

/// Finds which slot of an object's vtable holds a given function (searched in the player's vtable once).
static int FindSlot(const void* obj, const void* fn)
{
    if (!obj || !fn) return -1;
    void** vt = *(void***)obj;
    for (int i = 0; i < 400; i++)
    {
        void* f = NULL;
        __try { f = vt[i]; } __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
        if (f == fn) return i;
    }
    return -1;
}

static void* CallVirtualPtr(const void* obj, int slot) { return ((GetPtrFn)(*(void***)obj)[slot])(obj); }
static bool CallVirtualBool(const void* obj, int slot) { return ((BoolFn)(*(void***)obj)[slot])(obj); }

/// The game's class info: is <ci> the class <target> or derived from it? Walks the parent link at byte offset <off>, at most 16 steps.
static bool DerivesFromAt(const void* ci, const void* target, int off)
{
    for (int i = 0; i < 16 && ci; i++)
    {
        if (ci == target) return true;
        const void* next = NULL;
        __try { next = *(const void* const*)((const char*)ci + off); } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
        if (!Readable(next) || next == ci) return false;
        ci = next;
    }
    return false;
}

static bool DerivesFrom(const void* ci, const void* target) { return gParentOff >= 0 && DerivesFromAt(ci, target, gParentOff); }

/// Which field of the class info is the parent: the one through which Monster leads to Character (and Player to Character).
static int FindParentOffset(const void* monCi, const void* charCi, const void* playerCi)
{
    for (int off = 0; off <= 0x40; off += 8)
        if (DerivesFromAt(monCi, charCi, off) && (!playerCi || DerivesFromAt(playerCi, charCi, off))) return off;
    return -1;
}

/// Screen position of a world point given as region + region-relative position.
static bool ProjectPoint(const void* cam, const void* viewport, void* region, const float* pos, float* out)
{
    __declspec(align(16)) uint8_t wv[64] = { 0 };
    __declspec(align(16)) float res[4] = { 0 };
    fWorldVec3(wv, region, pos);
    fProject(cam, res, wv, viewport);
    out[0] = res[0]; out[1] = res[1];
    return true;
}

static void WorldPos(void* region, const float* pos, float* out)
{
    __declspec(align(16)) uint8_t wv[64] = { 0 };
    __declspec(align(16)) float res[4] = { 0 };
    fWorldVec3(wv, region, pos);
    fGetWorldPos(wv, res);
    out[0] = res[0]; out[1] = res[1]; out[2] = res[2];
}

static void ReadEntities(void* ge, void* en, void* player, const void* cam)
{
    gShm->entityCount = 0;
    gShm->seen = 0;
    if (!fGetEnts || !en || !cam) return;
    void* ge2 = fGetGraphicsEngine ? fGetGraphicsEngine(en) : NULL;
    void* canvas = ge2 && fGetCanvas ? fGetCanvas(ge2) : NULL;
    const void* vp = canvas && fGetViewport ? fGetViewport(canvas) : NULL;
    if (!vp) return;
    memcpy(gShm->viewport, vp, sizeof(gShm->viewport));

    // the player: world position and screen position
    __declspec(align(16)) uint8_t pc[256] = { 0 };
    fGetCoords(player, pc);
    void* preg = *(void**)pc;
    float* ppos = (float*)(pc + 8);
    WorldPos(preg, ppos, gShm->playerWorld);
    ProjectPoint(cam, vp, preg, ppos, gShm->playerScreen);
    gFrame.ok = Readable(preg);
    gFrame.cam = cam; gFrame.vp = vp; gFrame.region = preg;
    gFrame.pos[0] = ppos[0]; gFrame.pos[1] = ppos[1]; gFrame.pos[2] = ppos[2];

    const void* charCi = fStaticCharacter ? fStaticCharacter() : NULL;
    const void* monCi = fStaticMonster ? fStaticMonster() : NULL;
    if (gRttiSlot == -2) gRttiSlot = FindSlot(player, aPlayerRtti);
    if (gTargetSlot == -2) gTargetSlot = FindSlot(player, aCharTargetable);
    gShm->rttiSlot = gRttiSlot;
    gShm->targetSlot = gTargetSlot;
    if (gRttiSlot < 0 || !charCi) return;
    if (gParentOff == -2) gParentOff = FindParentOffset(monCi, charCi, CallVirtualPtr(player, gRttiSlot));
    gShm->classChainOk = gParentOff >= 0 ? 0x100u | (uint32_t)gParentOff : 0;
    if (gParentOff < 0) return;
    void* fm = fGetFactionMgr ? fGetFactionMgr(ge) : NULL;

    // everything within kRadius of the player (the game's own sphere query on the World)
    void* world = *(void**)((char*)en + kWorldOffset);
    if (!Readable(world) || !fSphere || !Readable(preg)) return;
    __declspec(align(16)) float sphere[4] = { ppos[0], ppos[1], ppos[2], kRadius };
    gEnts.e = gEnts.b;                               // reuse the buffer: the game appends to it
    fSphere(world, &gEnts, preg, sphere, false, 2);
    size_t n = gEnts.b ? (size_t)(gEnts.e - gEnts.b) : 0;
    gShm->seen = (uint32_t)n;
    uint32_t k = 0;
    for (size_t i = 0; i < n && k < 128; i++)
    {
        void* e = gEnts.b[i];
        if (!Readable(e) || e == player) continue;
        __try
        {
            const void* ci = CallVirtualPtr(e, gRttiSlot);
            if (!DerivesFrom(ci, charCi)) continue;           // only characters (monsters, NPCs, pets, other players)
            Shared::Ent& o = gShm->ents[k];
            memset(&o, 0, sizeof(o));
            o.ptr = (uint64_t)e;
            o.flags = 1;
            if (monCi && DerivesFrom(ci, monCi)) o.flags |= 32;
            if (fIsAttackable && fIsAttackable(ge, e)) o.flags |= 2;
            if (fm && fIsFriend && fIsFriend(fm, player, e)) o.flags |= 4;
            if (gTargetSlot >= 0 && CallVirtualBool(e, gTargetSlot)) o.flags |= 8;
            o.life = (float)fGetCurrentLife(e);
            o.lifeLimit = fGetLifeLimit(e);
            if (o.life > 0) o.flags |= 16;
            o.id = fGetObjectId ? fGetObjectId(e) : 0;
            __declspec(align(16)) uint8_t c[256] = { 0 };
            fGetCoords(e, c);
            void* reg = *(void**)c;
            float* pos = (float*)(c + 8);
            WorldPos(reg, pos, o.world);
            ProjectPoint(cam, vp, reg, pos, o.screen);
            float up[3] = { pos[0], pos[1] + 1.0f, pos[2] };
            ProjectPoint(cam, vp, reg, up, o.screenBody);
            float dx = o.world[0] - gShm->playerWorld[0], dy = o.world[1] - gShm->playerWorld[1], dz = o.world[2] - gShm->playerWorld[2];
            o.distance = sqrtf(dx * dx + dy * dy + dz * dz);
            k++;
        }
        __except (EXCEPTION_EXECUTE_HANDLER) { gShm->faults++; }
    }
    gShm->entityCount = k;
}

/// ObjectManager::GetObject as the game does it (inlined in Grim Dawn.exe, FUN_140009b70): under the manager's lock, an unordered_map
/// id -> Object* with an FNV-1a hash of the 4 id bytes; buckets at +0x48 ({first,last} node pairs), mask at +0x60, end sentinel at +0x38;
/// node = {next, prev, uint key @+0x10, Object* value @+0x18}.
static void* LookupObject(void* om, uint32_t id)
{
    if (!om || !id) return NULL;
    char* m = (char*)om;
    fCsEnter(m + 0x18, "GDLoLHook");
    void* found = NULL;
    __try
    {
        uint64_t h = 0xcbf29ce484222325ULL;
        for (int i = 0; i < 4; i++) { h ^= (id >> (8 * i)) & 0xFF; h *= 0x100000001b3ULL; }
        uint64_t mask = *(uint64_t*)(m + 0x60);
        char* buckets = *(char**)(m + 0x48);
        void* end = *(void**)(m + 0x38);
        uint64_t idx = h & mask;
        void* first = *(void**)(buckets + idx * 16);
        void* last = *(void**)(buckets + idx * 16 + 8);
        if (first != end)
        {
            void* stop = *(void**)last;                 // the node after the bucket's last one
            for (void* n = first; n != stop && n != end; n = *(void**)n)
                if (*(uint32_t*)((char*)n + 0x10) == id) { found = *(void**)((char*)n + 0x18); break; }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { found = NULL; }
    fCsExit(m + 0x18);
    return found;
}

/// Attack an entity the way a click on it does: the game's own mouse action, given the target's id and position instead of the cursor's.
static int ExecuteAttack(void* player, uint32_t targetId)
{
    if (!player) return -1;
    if (!fObjMgr || !fCsEnter || !fCsExit || !fGetControllerId || !fHandleMouse || !fSetNewMouseCommand || !fSetCombatEnemy) return -2;
    void* om = fObjMgr();
    void* ctrl = LookupObject(om, fGetControllerId(player));
    if (!Readable(ctrl)) return -2;
    if (aControllerPlayerClass && gParentOff >= 0 && !DerivesFrom(CallVirtualPtr(ctrl, 0), aControllerPlayerClass)) return -2;
    void* target = LookupObject(om, targetId);
    if (!Readable(target)) return -3;
    const void* charCi = fStaticCharacter ? fStaticCharacter() : NULL;
    if (gRttiSlot < 0 || !charCi || !DerivesFrom(CallVirtualPtr(target, gRttiSlot), charCi)) return -4;
    if (fGetCurrentLife(target) <= 0) return -4;
    __declspec(align(16)) uint8_t c[256] = { 0 };
    fGetCoords(target, c);
    __declspec(align(16)) uint8_t wv[64] = { 0 };
    fWorldVec3(wv, *(void**)c, (float*)(c + 8));
    uint32_t tid = targetId;
    bool flag = true;
    fSetCombatEnemy(ctrl, targetId);
    fSetNewMouseCommand(ctrl, true);
    bool ok = fHandleMouse(ctrl, false, true, false, false, wv, &tid, &flag);
    fSetNewMouseCommand(ctrl, false);
    if (fGetCombatEnemy) gShm->combatEnemy = fGetCombatEnemy(ctrl);
    // what the call left behind, for the app's report: the flag (false = taken by the state's first handler), the target id written back,
    // and the controller's command bytes (0x430 repeated, 0x431 new mouse command, 0x432 move command / last result)
    gShm->cmdTarget = tid;
    gShm->cmdKind = (flag ? 0x100u : 0u) | (uint32_t)(*((uint8_t*)ctrl + 0x432)) << 16 | 1u;
    return ok ? 1 : 0;
}

// ---------- game state and command queue ----------
typedef void*(__fastcall* WorldVec3DefFn)(void* self);
typedef bool(__fastcall* SetFromWorldFn)(void* self, const float* vec3, void* region);
typedef bool(__fastcall* PutOnFloorFn)(void* self);
typedef void*(__fastcall* GetWV3ByValFn)(const void* self, void* ret);
typedef void(__fastcall* GetRepeatFn)(void* ctrl, uint32_t* id, void* wv3);
typedef bool(__fastcall* PausedFn)();
typedef void*(__fastcall* GetHotSlotCtrlFn)(void* player);
typedef void*(__fastcall* GetSlotFn)(const void* hs);
typedef void*(__fastcall* GetSlotIdxFn)(const void* hs, uint32_t i);
typedef int(__fastcall* GetStatusFn)(const void* hs);
typedef void(__fastcall* ActivateFn)(void* hs);
typedef bool(__fastcall* SendSkillFn)(void* ctrl, void* character, bool a, bool b, uint32_t skill, const void* point, uint32_t* target);
static GetFloatFn fGetMana, fGetManaLimit;
static BoolFn fIsMoving;
static WorldVec3DefFn fWorldVec3Def;
static SetFromWorldFn fSetFromWorld;
static PutOnFloorFn fPutOnFloor;
static GetWV3ByValFn fGetTargetPoint;
static GetRepeatFn fGetRepeat;
static PausedFn fPaused;
static GetHotSlotCtrlFn fGetHotSlotCtrl;
static GetSlotFn fGetHpSlot, fGetMpSlot;
static GetSlotIdxFn fGetSlotOption;
static GetStatusFn fHpStatus, fMpStatus;
static ActivateFn fDrinkHp, fDrinkMp;
static SendSkillFn fSendSkill;
static void* aPotionCooldown;    // HotSlotOptionPotion::GetCooldownRemaining (to find its virtual slot)
static void* aCooldownBase;      // HotSlotOption::GetCooldownRemaining
static void* aCooldownSkill;     // HotSlotOptionSkill::GetCooldownRemaining
static void* aSkillIdBase;       // HotSlotOption::GetSkillId (virtual: slot found from it or the skill override)
static void* aSkillIdSkill;      // HotSlotOptionSkill::GetSkillId
static int gCooldownSlot = -2, gSkillIdSlot = -2;
static HWND gWindow;

static void* Controller(void* player)
{
    if (!fObjMgr || !fCsEnter || !fGetControllerId) return NULL;
    void* ctrl = LookupObject(fObjMgr(), fGetControllerId(player));
    if (!Readable(ctrl)) return NULL;
    if (aControllerPlayerClass && gParentOff >= 0 && !DerivesFrom(CallVirtualPtr(ctrl, 0), aControllerPlayerClass)) return NULL;
    return ctrl;
}

static void ToWorld(const void* wv, float* out)
{
    __declspec(align(16)) float r[4] = { 0 };
    fGetWorldPos(wv, r);
    out[0] = r[0]; out[1] = r[1]; out[2] = r[2];
}

/// The ground under a screen point: the point at the player's height whose projection is that screen point (Newton steps on the game's own
/// projection), then put on the floor by the game. Region-relative to the player's region. False when it does not converge.
static bool GroundAt(const void* cam, const void* vp, void* region, const float* ppos, float sx, float sy, float* out)
{
    float p[3] = { ppos[0], ppos[1], ppos[2] };
    const float h = 0.25f;
    for (int it = 0; it < 10; it++)
    {
        float s0[2], sxp[2], szp[2];
        ProjectPoint(cam, vp, region, p, s0);
        float ex = sx - s0[0], ey = sy - s0[1];
        if (ex * ex + ey * ey < 1.0f) { out[0] = p[0]; out[1] = p[1]; out[2] = p[2]; return true; }
        float px[3] = { p[0] + h, p[1], p[2] }, pz[3] = { p[0], p[1], p[2] + h };
        ProjectPoint(cam, vp, region, px, sxp);
        ProjectPoint(cam, vp, region, pz, szp);
        float a = (sxp[0] - s0[0]) / h, b = (szp[0] - s0[0]) / h, c = (sxp[1] - s0[1]) / h, d = (szp[1] - s0[1]) / h;
        float det = a * d - b * c;
        if (fabsf(det) < 1e-6f) return false;
        float dx = (d * ex - b * ey) / det, dz = (-c * ex + a * ey) / det;
        float len = sqrtf(dx * dx + dz * dz);
        if (len > 40.0f) { dx *= 40.0f / len; dz *= 40.0f / len; }
        p[0] += dx; p[2] += dz;
    }
    out[0] = p[0]; out[1] = p[1]; out[2] = p[2];
    return false;
}

static HWND GameWindow()
{
    if (gWindow && IsWindow(gWindow)) return gWindow;
    HWND f = GetForegroundWindow();
    DWORD pid = 0;
    if (f) GetWindowThreadProcessId(f, &pid);
    if (pid == GetCurrentProcessId()) gWindow = f;
    return gWindow;
}

/// Each part of the per-frame reading on its own: a fault in one (the game changed under it) must not leave the others unread.

static void PartBasics(void* player, void* ctrl, uint32_t& f)
{
    __try
    {
        if (fPaused && fPaused()) f |= 4;
        if (fIsMoving && fIsMoving(player)) f |= 8;
        gShm->mana = fGetMana ? fGetMana(player) : 0;
        gShm->manaLimit = fGetManaLimit ? fGetManaLimit(player) : 0;
        if (ctrl)
        {
            uint8_t* c = (uint8_t*)ctrl;
            if (c[0x432]) f |= 16;
            if (c[0x430]) f |= 32;
            gShm->hoverEnemy = fGetCombatEnemy ? fGetCombatEnemy(ctrl) : 0;
            gShm->hoverAlly = *(uint32_t*)(c + 0x46c);           // SetCombatAlly writes +0x46c (SetCombatEnemy +0x468)
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { gShm->faults++; }
}

static void PartCursorGround(const void* cam, const void* vp, void* preg, const float* ppos)
{
    gShm->cursorGroundOk = 0;
    __try
    {
        HWND w = GameWindow();
        POINT cp;
        if (w && GetCursorPos(&cp) && ScreenToClient(w, &cp))
        {
            float g[3];
            if (GroundAt(cam, vp, preg, ppos, (float)cp.x, (float)cp.y, g))
            {
                __declspec(align(16)) uint8_t wv[64] = { 0 };
                fWorldVec3(wv, preg, g);
                ToWorld(wv, gShm->cursorGround);
                gShm->cursorGroundOk = 1;
            }
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { gShm->cursorGroundOk = 0; gShm->faults++; }
}

static void PartHotbar(void* player)
{
    gShm->hpPotionCooldown = gShm->mpPotionCooldown = -1;
    __try
    {
        void* hs = fGetHotSlotCtrl ? fGetHotSlotCtrl(player) : NULL;
        if (!Readable(hs)) return;
        gShm->hpPotionStatus = fHpStatus ? fHpStatus(hs) : -1;
        gShm->mpPotionStatus = fMpStatus ? fMpStatus(hs) : -1;
        void* hp = fGetHpSlot ? fGetHpSlot(hs) : NULL;
        void* mp = fGetMpSlot ? fGetMpSlot(hs) : NULL;
        // the virtual slots are looked for again about once a second until found (an empty or unusual slot first)
        bool retry = (gShm->frame % 60) == 0;
        if ((gCooldownSlot == -2 || (gCooldownSlot == -1 && retry)) && Readable(hp))
        {
            // the potion slot's class may be one the game exe defines itself: look for any of the known implementations in its vtable
            gCooldownSlot = FindSlot(hp, aPotionCooldown);
            if (gCooldownSlot < 0) gCooldownSlot = FindSlot(hp, aCooldownBase);
            if (gCooldownSlot < 0) gCooldownSlot = FindSlot(hp, aCooldownSkill);
        }
        if (gCooldownSlot >= 0)
        {
            if (Readable(hp)) gShm->hpPotionCooldown = ((GetIntFn)(*(void***)hp)[gCooldownSlot])(hp);
            if (Readable(mp)) gShm->mpPotionCooldown = ((GetIntFn)(*(void***)mp)[gCooldownSlot])(mp);
        }
        for (uint32_t i = 0; i < 12; i++)
        {
            gShm->slotSkill[i] = 0;
            void* o = fGetSlotOption ? fGetSlotOption(hs, i) : NULL;
            if (!Readable(o)) continue;
            if (gSkillIdSlot == -2 && retry) { gSkillIdSlot = FindSlot(o, aSkillIdSkill); if (gSkillIdSlot < 0) gSkillIdSlot = FindSlot(o, aSkillIdBase); if (gSkillIdSlot < 0) gSkillIdSlot = -2; }
            if (gSkillIdSlot >= 0) gShm->slotSkill[i] = ((GetUIntFn)(*(void***)o)[gSkillIdSlot])(o);
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { gShm->faults++; }
}

static void PartRing(const void* cam, const void* vp, void* preg, const float* ppos)
{
    gShm->ringCount = 0;
    __try
    {
        float r = gShm->ringRadius;
        if (r > 0.5f && r < 80.0f)
        {
            for (int i = 0; i < 48; i++)
            {
                float a = 6.2831853f * i / 48.0f;
                float pt[3] = { ppos[0] + r * cosf(a), ppos[1], ppos[2] + r * sinf(a) };
                ProjectPoint(cam, vp, preg, pt, gShm->ring[i]);
            }
            gShm->ringCount = 48;
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER) { gShm->ringCount = 0; gShm->faults++; }
}

/// The helper's own view of the frame for the app: energy, movement, what the mouse points at, the ground under it, the hotbar, potions, ring.
/// (The controller's own target point and last click point are not read: their region can be one the game has unloaded since.)
static void ReadMore(void* player, const void* cam, const void* vp, void* preg, const float* ppos)
{
    uint32_t f = 1;
    __try { if (fGetCurrentLife(player) > 0) f |= 2; } __except (EXCEPTION_EXECUTE_HANDLER) { gShm->faults++; }
    void* ctrl = NULL;
    __try { ctrl = Controller(player); } __except (EXCEPTION_EXECUTE_HANDLER) { ctrl = NULL; gShm->faults++; }
    PartBasics(player, ctrl, f);
    gShm->flags2 = f;
    PartCursorGround(cam, vp, preg, ppos);
    PartHotbar(player);
    PartRing(cam, vp, preg, ppos);
}
/// Builds a WorldVec3 (in out, 64 bytes) for an absolute world position, put on the floor.
static bool WorldPoint(void* region, const float* world, void* out)
{
    if (!fWorldVec3Def || !fSetFromWorld) return false;
    fWorldVec3Def(out);
    if (!fSetFromWorld(out, world, region)) return false;
    if (fPutOnFloor) fPutOnFloor(out);
    return *(void**)out != NULL;
}

/// A ground click by command: the game's own mouse action on a point with no target, i.e. walk there.
static int MoveTo(void* player, const void* wv)
{
    void* ctrl = Controller(player);
    if (!ctrl || !fHandleMouse || !fSetNewMouseCommand) return -2;
    uint32_t tid = 0;
    bool flag = true;
    if (fSetCombatEnemy) fSetCombatEnemy(ctrl, 0);
    fSetNewMouseCommand(ctrl, true);
    fHandleMouse(ctrl, false, true, false, false, wv, &tid, &flag);
    fSetNewMouseCommand(ctrl, false);
    return 1;
}

static int RunOne(void* player, const void* cam, const void* vp, void* preg, const float* ppos, Shared::Cmd& c)
{
    switch (c.kind)
    {
        case 1: return ExecuteAttack(player, c.a);
        case 2:
        {
            __declspec(align(16)) uint8_t wv[64];
            float w[3] = { c.x, c.y, c.z };
            if (!WorldPoint(preg, w, wv)) return -6;
            return MoveTo(player, wv);
        }
        case 3:
        {
            float g[3];
            if (!GroundAt(cam, vp, preg, ppos, c.x, c.y, g)) return -6;
            __declspec(align(16)) uint8_t wv[64] = { 0 };
            fWorldVec3(wv, preg, g);
            if (fPutOnFloor) fPutOnFloor(wv);
            return MoveTo(player, wv);
        }
        case 4:
        case 5:
        {
            void* hs = fGetHotSlotCtrl ? fGetHotSlotCtrl(player) : NULL;
            if (!Readable(hs)) return -2;
            ActivateFn f = c.kind == 4 ? fDrinkHp : fDrinkMp;
            if (!f) return -2;
            f(hs);
            return 1;
        }
        case 6:
        {
            if (c.a >= 12 || !fSendSkill) return -2;
            uint32_t skill = gShm->slotSkill[c.a];
            if (!skill) return -7;
            void* ctrl = Controller(player);
            if (!ctrl) return -2;
            void* target = LookupObject(fObjMgr(), c.b);
            if (!Readable(target)) return -3;
            __declspec(align(16)) uint8_t co[256] = { 0 };
            fGetCoords(target, co);
            __declspec(align(16)) uint8_t wv[64] = { 0 };
            fWorldVec3(wv, *(void**)co, (float*)(co + 8));
            uint32_t tid = c.b;
            if (fSetCombatEnemy) fSetCombatEnemy(ctrl, c.b);
            return fSendSkill(ctrl, player, false, false, skill, wv, &tid) ? 1 : 0;
        }
    }
    return -8;
}

/// Runs every command the app queued since the last frame (game thread).
static void RunQueue(void* player, const void* cam, const void* vp, void* preg, const float* ppos)
{
    LONG w = gShm->qWrite;
    if (w - gShm->qRead > 16) gShm->qRead = w - 16;           // the app ran far ahead: the oldest are lost
    while (gShm->qRead != w)
    {
        Shared::Cmd& c = gShm->q[gShm->qRead & 15];
        int r = -5;
        __try { r = RunOne(player, cam, vp, preg, ppos, c); }
        __except (EXCEPTION_EXECUTE_HANDLER) { r = -5; gShm->faults++; }
        c.result = r;
        gShm->qRead = gShm->qRead + 1;
    }
}

static void Commands(void* player)
{
    LONG seq = gShm->cmdSeq;
    if (seq == gLastCmd) return;
    gLastCmd = seq;
    int r = -5;
    __try { r = gShm->cmdKind == 1 ? ExecuteAttack(player, gShm->cmdTarget) : 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { r = -5; gShm->faults++; }
    gShm->doneResult = r;
    gShm->doneSeq = seq;
}

static void FreeEnts()
{
    if (!gEnts.b) return;
    typedef void(__cdecl* FreeFn)(void*);
    HMODULE crt = GetModuleHandleA("ucrtbase.dll");
    FreeFn f = crt ? (FreeFn)GetProcAddress(crt, "free") : NULL;
    if (f)
    {
        void* mem = gEnts.b;
        size_t bytes = (size_t)((char*)gEnts.c - (char*)gEnts.b);
        if (bytes > 0xfff) mem = ((void**)gEnts.b)[-1];        // MSVC stores the real start before a big, 32-aligned block
        f(mem);
    }
    gEnts.b = gEnts.e = gEnts.c = NULL;
}

static void Tick()
{
    void* ge = *pGameEngine;
    void* en = *pEngine;
    gShm->gameEngine = (uint64_t)ge;
    gShm->engine = (uint64_t)en;
    void* player = ge ? fGetMainPlayer(ge) : NULL;
    gShm->player = (uint64_t)player;
    gShm->camera = ge ? (uint64_t)fGetCamera(ge) : 0;
    if (!player) { gShm->status &= ~8u; return; }
    gShm->status |= 8u;
    gShm->life = fGetCurrentLife(player);
    gShm->lifeLimit = fGetLifeLimit(player);
    gShm->lifeState = fGetLifeState(player);
    __declspec(align(16)) uint8_t buf[256];
    memset(buf, 0, sizeof(buf));
    fGetCoords(player, buf);
    memcpy(gShm->coords, buf, sizeof(gShm->coords));
    // the player's class info through its own vtable: GetRTTIClassInfo is a virtual function; find its slot by comparing with the export
    const void* cc = fStaticCharacter ? fStaticCharacter() : NULL;
    const void* mc = fStaticMonster ? fStaticMonster() : NULL;
    gShm->characterClassPtr = (uint64_t)cc;
    gShm->monsterClassPtr = (uint64_t)mc;
    if (cc) Copy(gShm->characterClass, cc, sizeof(gShm->characterClass));
    gFrame.ok = false;
    ReadEntities(ge, en, player, (const void*)gShm->camera);
    Commands(player);
    if (gFrame.ok)
    {
        __try { ReadMore(player, gFrame.cam, gFrame.vp, gFrame.region, gFrame.pos); }
        __except (EXCEPTION_EXECUTE_HANDLER) { gShm->faults++; }
        RunQueue(player, gFrame.cam, gFrame.vp, gFrame.region, gFrame.pos);
    }
}

static void __fastcall HookUpdate(void* self, const void* sphere, const void* frustum, bool a, bool b)
{
    InterlockedIncrement(&gInHook);
    gOrigUpdate(self, sphere, frustum, a, b);
    if (gShm && !gShm->quit)
    {
        InterlockedIncrement(&gShm->seq);
        __try { Tick(); }
        __except (EXCEPTION_EXECUTE_HANDLER) { gShm->faults++; }
        gShm->frame++;
        InterlockedIncrement(&gShm->seq);
        SetEvent(gFrameEvent);
    }
    InterlockedDecrement(&gInHook);
}

// ---------- import table hook ----------

static void** FindIatSlot(HMODULE exe, const char* dllName, const char* funcName)
{
    BYTE* base = (BYTE*)exe;
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    IMAGE_DATA_DIRECTORY dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT];
    if (!dir.VirtualAddress) return NULL;
    for (IMAGE_IMPORT_DESCRIPTOR* d = (IMAGE_IMPORT_DESCRIPTOR*)(base + dir.VirtualAddress); d->Name; d++)
    {
        if (lstrcmpiA((char*)(base + d->Name), dllName) != 0) continue;
        IMAGE_THUNK_DATA* names = (IMAGE_THUNK_DATA*)(base + (d->OriginalFirstThunk ? d->OriginalFirstThunk : d->FirstThunk));
        IMAGE_THUNK_DATA* slots = (IMAGE_THUNK_DATA*)(base + d->FirstThunk);
        for (; names->u1.AddressOfData; names++, slots++)
        {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            IMAGE_IMPORT_BY_NAME* ibn = (IMAGE_IMPORT_BY_NAME*)(base + names->u1.AddressOfData);
            if (lstrcmpA((char*)ibn->Name, funcName) == 0) return (void**)&slots->u1.Function;
        }
    }
    return NULL;
}

static bool WriteSlot(void** slot, void* value)
{
    DWORD old;
    if (!VirtualProtect(slot, sizeof(void*), PAGE_READWRITE, &old)) return false;
    InterlockedExchangePointer(slot, value);
    VirtualProtect(slot, sizeof(void*), old, &old);
    return true;
}

// ---------- life cycle ----------

static DWORD WINAPI Main(LPVOID)
{
    DWORD pid = GetCurrentProcessId();
    char name[96];
    wsprintfA(name, "Local\\GDLoLHook.%lu", pid);
    gMap = CreateFileMappingA(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, sizeof(Shared), name);
    if (!gMap) FreeLibraryAndExitThread(gSelf, 1);
    gShm = (Shared*)MapViewOfFile(gMap, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(Shared));
    if (!gShm) { CloseHandle(gMap); FreeLibraryAndExitThread(gSelf, 1); }
    memset(gShm, 0, sizeof(Shared));
    gShm->magic = 'GDLH';
    gShm->version = SHM_VERSION;
    gShm->status = 1;
    wsprintfA(name, "Local\\GDLoLHook.%lu.frame", pid);
    gFrameEvent = CreateEventA(NULL, FALSE, FALSE, name);
    wsprintfA(name, "Local\\GDLoLHook.%lu.quit", pid);
    gQuitEvent = CreateEventA(NULL, TRUE, FALSE, name);

    HMODULE game = GetModuleHandleA("Game.dll"), engine = GetModuleHandleA("Engine.dll"), exe = GetModuleHandleA(NULL);
    bool ok = game && engine;
    if (!game) AddMissing("Game.dll");
    if (!engine) AddMissing("Engine.dll");
    ok &= Resolve(game, "?gGameEngine@GAME@@3PEAVGameEngine@1@EA", pGameEngine);
    ok &= Resolve(engine, "?gEngine@GAME@@3PEAVEngine@1@EA", pEngine);
    ok &= Resolve(game, "?GetMainPlayer@GameEngine@GAME@@QEBAPEAVPlayer@2@XZ", fGetMainPlayer);
    ok &= Resolve(game, "?GetCamera@GameEngine@GAME@@QEAAPEAVGameCamera@2@XZ", fGetCamera);
    ok &= Resolve(game, "?GetCurrentLife@Character@GAME@@QEBA?BNXZ", fGetCurrentLife);
    ok &= Resolve(game, "?GetLifeLimit@Character@GAME@@QEBA?BMXZ", fGetLifeLimit);
    ok &= Resolve(game, "?GetLifeState@Character@GAME@@UEBA?BW4Character_LifeState@2@XZ", fGetLifeState);
    ok &= Resolve(engine, "?GetCoords@Entity@GAME@@QEBA?AVWorldCoords@2@XZ", fGetCoords);
    Resolve(game, "?GetStaticClassInfo@Character@GAME@@SAAEBVRTTI_ClassInfo@2@XZ", fStaticCharacter);
    Resolve(game, "?GetStaticClassInfo@Monster@GAME@@SAAEBVRTTI_ClassInfo@2@XZ", fStaticMonster);
    // characters around the player (optional: without them nothing is listed)
    Resolve(engine, "?GetEntitiesInPriorFrameFrustum@Engine@GAME@@QEAA_NAEAV?$vector@PEAVEntity@GAME@@@mem@@@Z", fGetEnts);
    Resolve(game, "?IsAttackableClass@GameEngine@GAME@@QEBA_NPEAVEntity@2@@Z", fIsAttackable);
    Resolve(game, "?GetFactionManager@GameEngine@GAME@@QEAAPEAVFactionManager@2@XZ", fGetFactionMgr);
    Resolve(game, "?IsFriend@FactionManager@GAME@@QEAA_NAEBVCharacter@2@0@Z", fIsFriend);
    Resolve(engine, "??0WorldVec3@GAME@@QEAA@PEAVRegion@1@AEBVVec3@1@@Z", fWorldVec3);
    Resolve(engine, "?GetWorldPosition@WorldVec3@GAME@@QEBA?AVVec3@2@XZ", fGetWorldPos);
    Resolve(engine, "?Project@WorldCamera@GAME@@QEBA?AVVec2@2@AEBVWorldVec3@2@AEBVViewport@2@@Z", fProject);
    Resolve(engine, "?GetGraphicsEngine@Engine@GAME@@QEBAPEAVGraphicsEngine@2@XZ", fGetGraphicsEngine);
    Resolve(engine, "?GetCanvas@GraphicsEngine@GAME@@QEAAAEAVGraphicsCanvas@2@XZ", fGetCanvas);
    Resolve(engine, "?GetViewport@GraphicsCanvas@GAME@@QEBAAEBVViewport@2@XZ", fGetViewport);
    Resolve(engine, "?GetObjectId@Object@GAME@@QEBAIXZ", fGetObjectId);
    Resolve(game, "?GetRTTIClassInfo@Player@GAME@@UEBAAEBVRTTI_ClassInfo@2@XZ", aPlayerRtti);
    Resolve(game, "?IsTargetable@Character@GAME@@UEBA_NXZ", aCharTargetable);
    Resolve(engine, "?GetEntitiesInSphere@World@GAME@@QEBAXAEAV?$vector@PEAVEntity@GAME@@@mem@@PEAVRegion@2@AEBVSphere@2@_NW4EntityListType@2@@Z", fSphere);
    // commands (optional: without them the app uses the mouse and keyboard)
    Resolve(engine, "?Get@?$Singleton@VObjectManager@GAME@@@GAME@@SAPEAVObjectManager@2@XZ", fObjMgr);
    Resolve(engine, "?Enter@CriticalSection@GAME@@QEAAXPEBD@Z", fCsEnter);
    Resolve(engine, "?Exit@CriticalSection@GAME@@QEAAXXZ", fCsExit);
    Resolve(game, "?GetControllerId@Character@GAME@@QEBA?BIXZ", fGetControllerId);
    Resolve(game, "?GetCombatEnemy@ControllerPlayer@GAME@@QEBAIXZ", fGetCombatEnemy);
    Resolve(game, "?SetCombatEnemy@ControllerPlayer@GAME@@QEAAXI@Z", fSetCombatEnemy);
    Resolve(game, "?SetNewMouseCommand@ControllerPlayer@GAME@@QEAAX_N@Z", fSetNewMouseCommand);
    Resolve(game, "?HandleActionFromMouse@ControllerPlayer@GAME@@QEAA_N_N000AEBVWorldVec3@2@AEAIPEA_N@Z", fHandleMouse);
    Resolve(game, "?classInfo@ControllerPlayer@GAME@@1VRTTI_ClassInfo@2@B", aControllerPlayerClass);
    // the rest of the game state and the command queue (each optional)
    Resolve(game, "?GetCurrentMana@Character@GAME@@QEBA?BMXZ", fGetMana);
    Resolve(game, "?GetManaLimit@Character@GAME@@QEBA?BMXZ", fGetManaLimit);
    Resolve(game, "?IsMoving@Character@GAME@@QEBA_NXZ", fIsMoving);
    Resolve(engine, "??0WorldVec3@GAME@@QEAA@XZ", fWorldVec3Def);
    Resolve(engine, "?SetFromWorldPosition@WorldVec3@GAME@@QEAA_NAEBVVec3@2@PEAVRegion@2@@Z", fSetFromWorld);
    Resolve(engine, "?PutOnFloor@WorldVec3@GAME@@QEAA_NXZ", fPutOnFloor);
    Resolve(game, "?GetTargetPoint@ControllerPlayer@GAME@@QEBA?AVWorldVec3@2@XZ", fGetTargetPoint);
    Resolve(game, "?GetMouseRepeatData@ControllerPlayer@GAME@@QEAAXAEAIAEAVWorldVec3@2@@Z", fGetRepeat);
    fPaused = engine ? (PausedFn)GetProcAddress(engine, "?IsGameTimePaused@GAME@@YA_NXZ") : NULL;
    if (!fPaused) Resolve(game, "?IsGameTimePaused@GAME@@YA_NXZ", fPaused);
    Resolve(game, "?GetPlayerHotSlotCtrl@Player@GAME@@QEAAAEAVPlayerHotSlotCtrl@2@XZ", fGetHotSlotCtrl);
    Resolve(game, "?GetHealthPotionSlot@PlayerHotSlotCtrl@GAME@@QEBAPEAVHotSlotOption@2@XZ", fGetHpSlot);
    Resolve(game, "?GetManaPotionSlot@PlayerHotSlotCtrl@GAME@@QEBAPEAVHotSlotOption@2@XZ", fGetMpSlot);
    Resolve(game, "?GetHotSlotOption@PlayerHotSlotCtrl@GAME@@QEBAPEAVHotSlotOption@2@I@Z", fGetSlotOption);
    Resolve(game, "?GetHealthPotionStatus@PlayerHotSlotCtrl@GAME@@QEBA?AW4HotSlotOptionStatus@2@XZ", fHpStatus);
    Resolve(game, "?GetManaPotionStatus@PlayerHotSlotCtrl@GAME@@QEBA?AW4HotSlotOptionStatus@2@XZ", fMpStatus);
    Resolve(game, "?ActivateHealthPotionSlot@PlayerHotSlotCtrl@GAME@@QEAAXXZ", fDrinkHp);
    Resolve(game, "?ActivateManaPotionSlot@PlayerHotSlotCtrl@GAME@@QEAAXXZ", fDrinkMp);
    Resolve(game, "?SendSkillAction@ControllerPlayer@GAME@@QEAA_NAEAVCharacter@2@_N1IAEBVWorldVec3@2@AEAI@Z", fSendSkill);
    Resolve(game, "?GetCooldownRemaining@HotSlotOptionPotion@GAME@@UEAAHXZ", aPotionCooldown);
    Resolve(game, "?GetCooldownRemaining@HotSlotOption@GAME@@UEAAHXZ", aCooldownBase);
    Resolve(game, "?GetCooldownRemaining@HotSlotOptionSkill@GAME@@UEAAHXZ", aCooldownSkill);
    Resolve(game, "?GetSkillId@HotSlotOption@GAME@@UEBAIXZ", aSkillIdBase);
    Resolve(game, "?GetSkillId@HotSlotOptionSkill@GAME@@UEBAIXZ", aSkillIdSkill);
    bool nearbyOk = fGetEnts && fSphere && fWorldVec3 && fGetWorldPos && fProject && fGetGraphicsEngine && fGetCanvas && fGetViewport && aPlayerRtti;
    if (!nearbyOk) fGetEnts = NULL;                      // incomplete: list nothing rather than half
    void* realUpdate = NULL;
    ok &= Resolve(engine, kUpdateName, realUpdate);
    if (ok) gShm->status |= 2u;

    if (ok)
    {
        gIatSlot = FindIatSlot(exe, "Engine.dll", kUpdateName);
        if (!gIatSlot) Note("Engine::Update is not imported by Grim Dawn.exe: hook not installed");
        else if (*gIatSlot != realUpdate) Note("Engine::Update import already redirected by something else: hook not installed");
        else
        {
            gOrigUpdate = (EngineUpdateFn)realUpdate;
            if (WriteSlot(gIatSlot, (void*)&HookUpdate)) { gShm->status |= 4u; Note("hooked"); }
            else { gIatSlot = NULL; Note("could not write the import table"); }
        }
    }
    else Note("exports missing: nothing installed");

    // wait for the app to ask for the unload
    while (WaitForSingleObject(gQuitEvent, 250) == WAIT_TIMEOUT)
        if (gShm->quit) break;
    gShm->status |= 16u;
    gShm->quit = 1;
    if (gIatSlot) WriteSlot(gIatSlot, (void*)gOrigUpdate);
    // a call that already entered the hook must finish before the code goes away
    for (int i = 0; i < 400 && gInHook; i++) Sleep(5);
    Sleep(50);
    __try { FreeEnts(); } __except (EXCEPTION_EXECUTE_HANDLER) {}
    UnmapViewOfFile(gShm);
    gShm = NULL;
    CloseHandle(gMap);
    CloseHandle(gFrameEvent);
    CloseHandle(gQuitEvent);
    FreeLibraryAndExitThread(gSelf, 0);
    return 0;
}

BOOL WINAPI DllMain(HINSTANCE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        gSelf = h;
        DisableThreadLibraryCalls(h);
        HANDLE t = CreateThread(NULL, 0, Main, NULL, 0, NULL);     // starts after the loader lock is released
        if (t) CloseHandle(t);
    }
    return TRUE;
}
