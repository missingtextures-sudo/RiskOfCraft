//! RiskOfCraft's link: Risk of Rain 2's end of the shared memory Minecraft opens.
//!
//! Risk of Rain 2's BepInEx plugin loads this DLL and calls the `rc_*` functions every frame. The
//! layout comes from design/protocol_*.json (src/proto.rs is generated from it, as is the plugin's
//! C# mirror), so the two can't drift. Clocks are the guest's own: GetTickCount64 for heartbeats,
//! QueryPerformanceCounter for tick timestamps.

pub mod proto;
pub mod rings;

use core::ffi::c_void;
use core::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use proto::*;

#[allow(non_snake_case)]
#[link(name = "kernel32")]
extern "system" {
    fn CreateFileMappingW(file: isize, attributes: *const c_void, protect: u32, size_high: u32, size_low: u32, name: *const u16) -> isize;
    fn MapViewOfFile(mapping: isize, access: u32, off_high: u32, off_low: u32, bytes: usize) -> *mut c_void;
    fn UnmapViewOfFile(view: *const c_void) -> i32;
    fn CloseHandle(handle: isize) -> i32;
    fn GetLastError() -> u32;
    fn GetTickCount64() -> u64;
    fn QueryPerformanceCounter(count: *mut i64) -> i32;
    fn QueryPerformanceFrequency(freq: *mut i64) -> i32;
    fn GetCurrentProcessId() -> u32;
}

const PAGE_READWRITE: u32 = 0x04;
const FILE_MAP_ALL_ACCESS: u32 = 0xF001F;
const ERROR_ALREADY_EXISTS: u32 = 183;
const OVERLAY_DIRTY: u32 = 1 << 2;
const OVERLAY_SLOT_BYTES: usize = (MAX_OVERLAY_W * MAX_OVERLAY_H * 4) as usize;

struct Link {
    mapping: isize,
    base: *mut u8,
    overlay_front: u32,
    command_id: u32,
    last_error: u32,
}

static mut LINK: Link = Link { mapping: 0, base: core::ptr::null_mut(), overlay_front: 2, command_id: 0, last_error: 0 };

#[inline]
unsafe fn link() -> &'static mut Link {
    &mut *core::ptr::addr_of_mut!(LINK)
}

#[inline]
unsafe fn at(off: usize) -> *mut u8 {
    link().base.add(off)
}

unsafe fn header() -> *mut Header {
    at(OFF_HEADER) as *mut Header
}

// ---- lifetime ------------------------------------------------------------------------------

/// Protocol version this DLL speaks.
#[no_mangle]
pub extern "C" fn rc_version() -> u32 {
    VERSION as u32
}

/// Bytes of the mapping.
#[no_mangle]
pub extern "C" fn rc_mapping_bytes() -> u64 {
    MAPPING_BYTES
}

/// Creates (or reopens) the mapping. 1 created, 2 reused (Minecraft still had it open), 0 failed
/// (see rc_last_error). Everything the host owns is reset, the magic is written last.
#[no_mangle]
pub unsafe extern "C" fn rc_create(name: *const u16) -> u32 {
    let l = link();
    if !l.base.is_null() {
        return 2;
    }
    let size = MAPPING_BYTES;
    let mapping = CreateFileMappingW(-1, core::ptr::null(), PAGE_READWRITE, (size >> 32) as u32, size as u32, name);
    let err = GetLastError();
    if mapping == 0 {
        l.last_error = err;
        return 0;
    }
    let base = MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, 0) as *mut u8;
    if base.is_null() {
        l.last_error = GetLastError();
        CloseHandle(mapping);
        return 0;
    }
    l.mapping = mapping;
    l.base = base;
    l.overlay_front = 2;
    // A stale mapping survives while Minecraft still has it open from an earlier RoR2 run.
    for (off, bytes) in [
        (OFF_HOST_STATE, BYTES_HOST_STATE),
        (OFF_OVERLAY_CTL, 0x40),
        (OFF_WATER_GRID, 0x10),
        (OFF_INPUT_RING, RING_DATA_OFF as usize),
        (OFF_COLLISION_RING, RING_DATA_OFF as usize),
        (OFF_ACTOR_TABLE, 0x40),
        (OFF_EVENT_RING, RING_DATA_OFF as usize),
        (OFF_COMMAND_RING, RING_DATA_OFF as usize),
        (OFF_WORLD_ENTITIES, 0x40),
        (OFF_RENDER_RING, RING_DATA_OFF as usize),
    ] {
        core::ptr::write_bytes(base.add(off), 0, bytes);
    }
    let h = header();
    (*h).version = VERSION as u32;
    (*h).hostPid = GetCurrentProcessId();
    (*h).hostHeartbeatMs = GetTickCount64();
    (*(core::ptr::addr_of_mut!((*h).magic) as *const AtomicU32)).store(MAGIC as u32, Ordering::Release);
    if err == ERROR_ALREADY_EXISTS { 2 } else { 1 }
}

#[no_mangle]
pub unsafe extern "C" fn rc_close() {
    let l = link();
    if !l.base.is_null() {
        (*(core::ptr::addr_of_mut!((*header()).magic) as *const AtomicU32)).store(0, Ordering::Release);
        UnmapViewOfFile(l.base as *const c_void);
        l.base = core::ptr::null_mut();
    }
    if l.mapping != 0 {
        CloseHandle(l.mapping);
        l.mapping = 0;
    }
}

#[no_mangle]
pub unsafe extern "C" fn rc_last_error() -> u32 {
    link().last_error
}

#[no_mangle]
pub unsafe extern "C" fn rc_is_open() -> u32 {
    (!link().base.is_null()) as u32
}

// ---- clocks and liveness -------------------------------------------------------------------

#[no_mangle]
pub extern "C" fn rc_tick_ms() -> u64 {
    unsafe { GetTickCount64() }
}

#[no_mangle]
pub extern "C" fn rc_qpc() -> i64 {
    let mut c = 0i64;
    unsafe { QueryPerformanceCounter(&mut c) };
    c
}

#[no_mangle]
pub extern "C" fn rc_qpc_frequency() -> i64 {
    let mut f = 0i64;
    unsafe { QueryPerformanceFrequency(&mut f) };
    f
}

#[no_mangle]
pub unsafe extern "C" fn rc_heartbeat() {
    if link().base.is_null() {
        return;
    }
    (*(core::ptr::addr_of_mut!((*header()).hostHeartbeatMs) as *const AtomicU64)).store(GetTickCount64(), Ordering::Release);
}

/// 1 while Minecraft's heartbeat is non-zero and younger than `timeout_ms`.
#[no_mangle]
pub unsafe extern "C" fn rc_guest_alive(timeout_ms: u64) -> u32 {
    if link().base.is_null() {
        return 0;
    }
    let last = (*(core::ptr::addr_of_mut!((*header()).guestHeartbeatMs) as *const AtomicU64)).load(Ordering::Acquire);
    (last != 0 && GetTickCount64().wrapping_sub(last) < timeout_ms) as u32
}

#[no_mangle]
pub unsafe extern "C" fn rc_guest_pid() -> u32 {
    if link().base.is_null() {
        return 0;
    }
    core::ptr::read_volatile(core::ptr::addr_of!((*header()).guestPid))
}

// ---- state slots ---------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn rc_write_host_state(state: *const HostState) {
    if link().base.is_null() || state.is_null() {
        return;
    }
    rings::seqlock_write(at(OFF_HOST_STATE), &*state);
}

/// 1 on a clean read; 0 on a torn one (keep last frame's).
#[no_mangle]
pub unsafe extern "C" fn rc_read_guest_state(out: *mut GuestState) -> u32 {
    if link().base.is_null() || out.is_null() {
        return 0;
    }
    rings::seqlock_read(at(OFF_GUEST_STATE), &mut *out) as u32
}

/// Publishes the nearby RoR2 monsters (at most ENTRIES_ACTOR_TABLE).
#[no_mangle]
pub unsafe extern "C" fn rc_write_actors(records: *const ActorRecord, count: u32) {
    if link().base.is_null() {
        return;
    }
    let n = (count as usize).min(ENTRIES_ACTOR_TABLE);
    let slice = if n == 0 || records.is_null() { &[][..] } else { core::slice::from_raw_parts(records, n) };
    rings::seqlock_write_table(at(OFF_ACTOR_TABLE), 0x40, slice);
}

/// Copies the world-entities table: up to `max` records, the count, and the block selection box
/// (6 floats: min xyz, max xyz) when Minecraft shows one. 0 on a torn read.
#[no_mangle]
pub unsafe extern "C" fn rc_read_world_entities(out: *mut WorldEntity, max: u32, count: *mut u32, has_selection: *mut u32, selection: *mut f32) -> u32 {
    if link().base.is_null() {
        return 0;
    }
    let table = at(OFF_WORLD_ENTITIES);
    let seq = &*(table as *const AtomicU32);
    for _ in 0..16 {
        let s1 = seq.load(Ordering::Acquire);
        if s1 & 1 != 0 {
            core::hint::spin_loop();
            continue;
        }
        let n = core::ptr::read_volatile(table.add(4) as *const u32).min(ENTRIES_WORLD_ENTITIES as u32).min(max);
        if !out.is_null() && n > 0 {
            core::ptr::copy_nonoverlapping(table.add(0x40) as *const WorldEntity, out, n as usize);
        }
        let has = core::ptr::read_volatile(table.add(8) as *const u32);
        if !selection.is_null() {
            core::ptr::copy_nonoverlapping(table.add(12) as *const f32, selection, 6);
        }
        core::sync::atomic::fence(Ordering::Acquire);
        if seq.load(Ordering::Relaxed) == s1 {
            if !count.is_null() {
                *count = n;
            }
            if !has_selection.is_null() {
                *has_selection = has;
            }
            return 1;
        }
    }
    0
}

// ---- rings ---------------------------------------------------------------------------------

#[no_mangle]
pub unsafe extern "C" fn rc_push_input(e: *const InputEvent) -> u32 {
    if link().base.is_null() || e.is_null() {
        return 0;
    }
    rings::entry_push(at(OFF_INPUT_RING), ENTRIES_INPUT_RING, &*e) as u32
}

#[no_mangle]
pub unsafe extern "C" fn rc_pop_event(out: *mut GuestEvent) -> u32 {
    if link().base.is_null() || out.is_null() {
        return 0;
    }
    rings::entry_pop(at(OFF_EVENT_RING), ENTRIES_EVENT_RING, &mut *out) as u32
}

/// Queues a Minecraft command (UTF-8, no leading slash) for the guest to run as the player.
/// 0 when the ring is full or the text is too long.
#[no_mangle]
pub unsafe extern "C" fn rc_push_command(text: *const u8, len: u32) -> u32 {
    let l = link();
    if l.base.is_null() || text.is_null() {
        return 0;
    }
    let mut cmd = HostCommand::default();
    if len as usize > cmd.text.len() {
        return 0;
    }
    l.command_id = l.command_id.wrapping_add(1);
    cmd.id = l.command_id;
    cmd.length = len;
    core::ptr::copy_nonoverlapping(text, cmd.text.as_mut_ptr(), len as usize);
    rings::entry_push(at(OFF_COMMAND_RING), ENTRIES_COMMAND_RING, &cmd) as u32
}

#[no_mangle]
pub unsafe extern "C" fn rc_write_collision(kind: u32, payload: *const u8, bytes: u32) -> u32 {
    if link().base.is_null() {
        return 0;
    }
    rings::byte_write(at(OFF_COLLISION_RING), (BYTES_COLLISION_RING - RING_DATA_OFF as usize) as u64, kind, payload, bytes) as u32
}

#[no_mangle]
pub unsafe extern "C" fn rc_collision_backlog() -> u64 {
    if link().base.is_null() {
        return 0;
    }
    rings::byte_backlog(at(OFF_COLLISION_RING))
}

/// The next render message, without consuming it. 0 when the ring is empty.
#[no_mangle]
pub unsafe extern "C" fn rc_render_peek(kind: *mut u32, payload: *mut *const u8, bytes: *mut u32) -> u32 {
    if link().base.is_null() {
        return 0;
    }
    match rings::byte_peek(at(OFF_RENDER_RING), (BYTES_RENDER_RING - RING_DATA_OFF as usize) as u64) {
        Some((k, p, n)) => {
            *kind = k;
            *payload = p;
            *bytes = n;
            1
        }
        None => 0,
    }
}

/// Consumes the message rc_render_peek returned; returns the bytes it took in the ring.
#[no_mangle]
pub unsafe extern "C" fn rc_render_consume() -> u64 {
    if link().base.is_null() {
        return 0;
    }
    rings::byte_consume(at(OFF_RENDER_RING), (BYTES_RENDER_RING - RING_DATA_OFF as usize) as u64)
}

// ---- overlay triple buffer: the host owns the front slot, Minecraft the back ---------------

/// Takes Minecraft's newest HUD frame if there is one (1), else keeps the current front (0).
#[no_mangle]
pub unsafe extern "C" fn rc_overlay_acquire() -> u32 {
    let l = link();
    if l.base.is_null() {
        return 0;
    }
    let state = &*(at(OFF_OVERLAY_CTL) as *const AtomicU32);
    if state.load(Ordering::Acquire) & OVERLAY_DIRTY == 0 {
        return 0;
    }
    let old = state.swap(l.overlay_front, Ordering::AcqRel);
    l.overlay_front = old & 3;
    1
}

#[no_mangle]
pub unsafe extern "C" fn rc_overlay_reset() {
    let l = link();
    if l.base.is_null() {
        return;
    }
    (*(at(OFF_OVERLAY_CTL) as *const AtomicU32)).store(0, Ordering::Release);
    l.overlay_front = 2;
}

#[no_mangle]
pub unsafe extern "C" fn rc_overlay_front_header(out: *mut OverlaySlotHdr) {
    let l = link();
    if l.base.is_null() || out.is_null() || l.overlay_front > 2 {
        return;
    }
    *out = core::ptr::read_volatile((at(OFF_OVERLAY_SLOT_HDR) as *const OverlaySlotHdr).add(l.overlay_front as usize));
}

#[no_mangle]
pub unsafe extern "C" fn rc_overlay_front_pixels() -> *const u8 {
    let l = link();
    if l.base.is_null() || l.overlay_front > 2 {
        return core::ptr::null();
    }
    at(OFF_OVERLAY_PIXELS + OVERLAY_SLOT_BYTES * l.overlay_front as usize)
}

// ---- self checks ---------------------------------------------------------------------------

/// Size of a protocol struct by name (as the sheet names it), or -1. The plugin compares these
/// with its own C# structs at startup.
#[no_mangle]
pub unsafe extern "C" fn rc_struct_size(name: *const u8, len: u32) -> i32 {
    let name = core::str::from_utf8(core::slice::from_raw_parts(name, len as usize)).unwrap_or("");
    STRUCT_SIZES.iter().find(|(n, _)| *n == name).map(|(_, s)| *s as i32).unwrap_or(-1)
}

/// Runs the rings through wrap-arounds on private buffers. 0 = pass; bit 0 entry ring, bit 1
/// byte ring, bit 2 command ring, bit 3 field layout.
#[no_mangle]
pub extern "C" fn rc_self_test() -> u32 {
    let mut fails = 0;
    if !selftest::entry_ring() {
        fails |= 1;
    }
    if !selftest::byte_ring() {
        fails |= 2;
    }
    if !selftest::command_ring() {
        fails |= 4;
    }
    if !selftest::layout() {
        fails |= 8;
    }
    fails
}

pub mod selftest {
    use super::*;

    fn buffer(bytes: usize) -> Vec<u64> {
        vec![0u64; (bytes + 7) / 8]
    }

    pub fn entry_ring() -> bool {
        const ENTRIES: usize = 8;
        let mut buf = buffer(0x80 + ENTRIES * 16);
        let ring = buf.as_mut_ptr() as *mut u8;
        let mut ok = true;
        let (mut next, mut expect) = (0i32, 0i32);
        unsafe {
            for _ in 0..3 {
                for _ in 0..5 {
                    let e = InputEvent { r#type: 1, code: next as u16, a: next * 7, b: -next, c: next ^ 0x55 };
                    ok &= rings::entry_push(ring, ENTRIES, &e);
                    next += 1;
                }
                for _ in 0..5 {
                    let mut e = InputEvent::default();
                    ok &= rings::entry_pop(ring, ENTRIES, &mut e) && e.code == expect as u16 && e.a == expect * 7 && e.b == -expect && e.c == (expect ^ 0x55);
                    expect += 1;
                }
                let mut e = InputEvent::default();
                ok &= !rings::entry_pop(ring, ENTRIES, &mut e);
            }
            for _ in 0..ENTRIES {
                ok &= rings::entry_push(ring, ENTRIES, &InputEvent::default());
            }
            ok &= !rings::entry_push(ring, ENTRIES, &InputEvent::default());
        }
        ok
    }

    pub fn byte_ring() -> bool {
        const DATA: u64 = 256;
        let mut buf = buffer(0x80 + DATA as usize);
        let ring = buf.as_mut_ptr() as *mut u8;
        let mut ok = true;
        let (mut written, mut read) = (0u32, 0u32);
        let mut payload = [0u8; 40];
        unsafe {
            for _ in 0..12 {
                for _ in 0..2 {
                    for (k, b) in payload.iter_mut().enumerate() {
                        *b = (written.wrapping_mul(31) as usize + k) as u8;
                    }
                    ok &= rings::byte_write(ring, DATA, written + 1, payload.as_ptr(), 40);
                    written += 1;
                }
                while let Some((kind, p, n)) = rings::byte_peek(ring, DATA) {
                    ok &= kind == read + 1 && n == 40;
                    for k in 0..40usize {
                        ok &= *p.add(k) == (read.wrapping_mul(31) as usize + k) as u8;
                    }
                    rings::byte_consume(ring, DATA);
                    read += 1;
                }
                ok &= read == written;
            }
            ok &= *(ring as *const u64) > DATA; // it wrapped
        }
        ok
    }

    pub fn command_ring() -> bool {
        let mut buf = buffer(0x80 + ENTRIES_COMMAND_RING * core::mem::size_of::<HostCommand>());
        let ring = buf.as_mut_ptr() as *mut u8;
        let mut ok = true;
        unsafe {
            for i in 0..(ENTRIES_COMMAND_RING * 3) as u32 {
                let text = format!("give @s minecraft:bread {}", i);
                let mut c = HostCommand::default();
                c.id = i;
                c.length = text.len() as u32;
                c.text[..text.len()].copy_from_slice(text.as_bytes());
                ok &= rings::entry_push(ring, ENTRIES_COMMAND_RING, &c);
                let mut back = HostCommand::default();
                ok &= rings::entry_pop(ring, ENTRIES_COMMAND_RING, &mut back);
                ok &= back.id == i && &back.text[..back.length as usize] == text.as_bytes();
            }
        }
        ok
    }

    /// Every field sits where the sheet says (the generator asserts sizes at compile time).
    pub fn layout() -> bool {
        macro_rules! off {
            ($t:ty, $f:ident) => {{
                let v = core::mem::MaybeUninit::<$t>::uninit();
                let base = v.as_ptr() as usize;
                let field = unsafe { core::ptr::addr_of!((*v.as_ptr()).$f) } as usize;
                field - base
            }};
        }
        let checks: &[(&str, &str, usize)] = &[
            ("Header", "guestHeartbeatMs", off!(Header, guestHeartbeatMs)),
            ("HostState", "posX", off!(HostState, posX)),
            ("HostState", "teleportSeq", off!(HostState, teleportSeq)),
            ("HostState", "gameHour", off!(HostState, gameHour)),
            ("GuestState", "frameCounter", off!(GuestState, frameCounter)),
            ("GuestState", "eyeX", off!(GuestState, eyeX)),
            ("GuestState", "tickQpc", off!(GuestState, tickQpc)),
            ("GuestState", "curX", off!(GuestState, curX)),
            ("GuestState", "cameraMode", off!(GuestState, cameraMode)),
            ("GuestState", "cameraDistance", off!(GuestState, cameraDistance)),
            ("ActorRecord", "level", off!(ActorRecord, level)),
            ("ActorRecord", "name", off!(ActorRecord, name)),
            ("WorldEntity", "uv", off!(WorldEntity, uv)),
            ("WorldEntity", "tint", off!(WorldEntity, tint)),
            ("ColBlock", "bits", off!(ColBlock, bits)),
            ("ColTri", "flags", off!(ColTri, flags)),
            ("RenScene", "batchCount", off!(RenScene, batchCount)),
            ("HostCommand", "text", off!(HostCommand, text)),
        ];
        checks.iter().all(|(s, f, actual)| FIELD_OFFSETS.iter().any(|(s2, f2, o)| s2 == s && f2 == f && o == actual))
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn self_test_passes() {
        assert_eq!(super::rc_self_test(), 0);
    }

    #[test]
    fn mapping_fits() {
        assert_eq!(super::proto::OFF_RENDER_RING + super::proto::BYTES_RENDER_RING, super::proto::MAPPING_BYTES as usize);
    }
}
