//! The protocol's shared-memory shapes on raw pointers: seqlock slots, entry rings and byte rings.
//! Every function works on a base pointer, so the same code runs on the mapping and on a test buffer.

use crate::proto::{MsgHeader, RING_DATA_OFF, RING_HEAD_OFF, RING_TAIL_OFF};
use core::sync::atomic::{fence, AtomicU32, AtomicU64, Ordering};

#[inline]
unsafe fn a32<'a>(p: *mut u8) -> &'a AtomicU32 {
    &*(p as *const AtomicU32)
}

#[inline]
unsafe fn a64<'a>(p: *mut u8) -> &'a AtomicU64 {
    &*(p as *const AtomicU64)
}

const HEAD: usize = RING_HEAD_OFF as usize;
const TAIL: usize = RING_TAIL_OFF as usize;
const DATA: usize = RING_DATA_OFF as usize;

// ---- seqlock: u32 seq at offset 0, odd while the writer is inside ----

/// Writes `value` (whose first 4 bytes are the seq field) into the slot.
pub unsafe fn seqlock_write<T: Copy>(slot: *mut u8, value: &T) {
    let seq = a32(slot);
    let s = seq.load(Ordering::Relaxed);
    seq.store(s.wrapping_add(1), Ordering::Relaxed);
    fence(Ordering::SeqCst);
    let n = core::mem::size_of::<T>();
    core::ptr::copy_nonoverlapping((value as *const T as *const u8).add(4), slot.add(4), n - 4);
    fence(Ordering::Release);
    seq.store(s.wrapping_add(2), Ordering::Release);
}

/// Copies the slot out; false on a torn read (the caller keeps last frame's value).
pub unsafe fn seqlock_read<T: Copy + Default>(slot: *mut u8, out: &mut T) -> bool {
    let seq = a32(slot);
    for _ in 0..64 {
        let s1 = seq.load(Ordering::Acquire);
        if s1 & 1 != 0 {
            core::hint::spin_loop();
            continue;
        }
        let mut copy = T::default();
        core::ptr::copy_nonoverlapping(slot, &mut copy as *mut T as *mut u8, core::mem::size_of::<T>());
        fence(Ordering::Acquire);
        if seq.load(Ordering::Relaxed) == s1 {
            *out = copy;
            return true;
        }
    }
    false
}

/// Seqlock write of a `u32 seq, u32 count` table header plus `count` records at `records_off`.
pub unsafe fn seqlock_write_table<T: Copy>(table: *mut u8, records_off: usize, records: &[T]) {
    let seq = a32(table);
    let s = seq.load(Ordering::Relaxed);
    seq.store(s.wrapping_add(1), Ordering::Relaxed);
    fence(Ordering::SeqCst);
    core::ptr::copy_nonoverlapping(records.as_ptr() as *const u8, table.add(records_off), core::mem::size_of_val(records));
    *(table.add(4) as *mut u32) = records.len() as u32;
    fence(Ordering::Release);
    seq.store(s.wrapping_add(2), Ordering::Release);
}

// ---- ring of fixed-size entries; `entries` is a power of two ----

pub unsafe fn entry_push<T: Copy>(ring: *mut u8, entries: usize, entry: &T) -> bool {
    let head = a64(ring.add(HEAD)).load(Ordering::Relaxed);
    let tail = a64(ring.add(TAIL)).load(Ordering::Acquire);
    if head.wrapping_sub(tail) >= entries as u64 {
        return false;
    }
    let slot = (ring.add(DATA) as *mut T).add((head as usize) & (entries - 1));
    core::ptr::write_unaligned(slot, *entry);
    a64(ring.add(HEAD)).store(head + 1, Ordering::Release);
    true
}

pub unsafe fn entry_pop<T: Copy>(ring: *mut u8, entries: usize, out: &mut T) -> bool {
    let head = a64(ring.add(HEAD)).load(Ordering::Acquire);
    let mut tail = a64(ring.add(TAIL)).load(Ordering::Relaxed);
    if tail >= head {
        return false;
    }
    if head - tail > entries as u64 {
        tail = head - entries as u64; // the producer lapped us; drop the oldest
    }
    let slot = (ring.add(DATA) as *const T).add((tail as usize) & (entries - 1));
    *out = core::ptr::read_unaligned(slot);
    a64(ring.add(TAIL)).store(tail + 1, Ordering::Release);
    true
}

// ---- byte ring: 8-byte aligned {type, payloadBytes} messages; type 0 = skip to the start ----

const HDR: u64 = core::mem::size_of::<MsgHeader>() as u64;

#[inline]
fn msg_bytes(payload: u64) -> u64 {
    (HDR + payload + 7) & !7
}

pub unsafe fn byte_write(ring: *mut u8, data_bytes: u64, kind: u32, payload: *const u8, payload_bytes: u32) -> bool {
    let data = ring.add(DATA);
    let size = data_bytes;
    let need = msg_bytes(payload_bytes as u64);
    if need > size / 2 {
        return false;
    }
    let mut head = a64(ring.add(HEAD)).load(Ordering::Relaxed);
    let tail = a64(ring.add(TAIL)).load(Ordering::Acquire);
    let mut pos = head % size;
    let pad = if pos + need > size { size - pos } else { 0 };
    if size - (head - tail) < need + pad {
        return false;
    }
    if pad != 0 {
        core::ptr::write_unaligned(data.add(pos as usize) as *mut MsgHeader, MsgHeader { r#type: 0, payloadBytes: 0 });
        head += pad;
        pos = 0;
    }
    core::ptr::write_unaligned(data.add(pos as usize) as *mut MsgHeader, MsgHeader { r#type: kind, payloadBytes: payload_bytes });
    if payload_bytes != 0 {
        core::ptr::copy_nonoverlapping(payload, data.add((pos + HDR) as usize), payload_bytes as usize);
    }
    a64(ring.add(HEAD)).store(head + need, Ordering::Release);
    true
}

/// Unconsumed bytes in a byte ring.
pub unsafe fn byte_backlog(ring: *mut u8) -> u64 {
    a64(ring.add(HEAD)).load(Ordering::Relaxed).wrapping_sub(a64(ring.add(TAIL)).load(Ordering::Acquire))
}

/// The next message without consuming it: (type, payload, bytes). Pads are skipped (and consumed).
pub unsafe fn byte_peek(ring: *mut u8, data_bytes: u64) -> Option<(u32, *const u8, u32)> {
    let data = ring.add(DATA);
    let size = data_bytes;
    loop {
        let head = a64(ring.add(HEAD)).load(Ordering::Acquire);
        let tail = a64(ring.add(TAIL)).load(Ordering::Relaxed);
        if tail >= head {
            return None;
        }
        let pos = tail % size;
        let hdr = core::ptr::read_unaligned(data.add(pos as usize) as *const MsgHeader);
        if hdr.r#type == 0 {
            a64(ring.add(TAIL)).store(tail + (size - pos), Ordering::Release);
            continue;
        }
        return Some((hdr.r#type, data.add((pos + HDR) as usize), hdr.payloadBytes));
    }
}

/// Consumes the message `byte_peek` returned; returns its size in the ring.
pub unsafe fn byte_consume(ring: *mut u8, data_bytes: u64) -> u64 {
    let data = ring.add(DATA);
    let tail = a64(ring.add(TAIL)).load(Ordering::Relaxed);
    let pos = tail % data_bytes;
    let hdr = core::ptr::read_unaligned(data.add(pos as usize) as *const MsgHeader);
    let n = msg_bytes(hdr.payloadBytes as u64);
    a64(ring.add(TAIL)).store(tail + n, Ordering::Release);
    n
}
