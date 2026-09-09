// Reads only a Minecraft world-download ZIP's central directory (a small tail slice, not the whole
// file) to derive each dimension's density-weighted centroid from region file names, plus the
// LastPlayed date from level.dat. Returns { candidates: [{dimension,centerX,centerZ}], lastPlayed }.
window.atlasInspectWdl = async function (inputId, fileIndex = 0) {
    const empty = { candidates: [], unknownDimensions: [], lastPlayed: null };
    try {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[fileIndex];
        if (!file) return empty;

        const size = file.size;
        const readView = async (start, end) =>
            new DataView(await file.slice(start, Math.min(end, size)).arrayBuffer());

        // End Of Central Directory record lives in the last 22..(22+65535) bytes.
        const tailLen = Math.min(size, 65557 + 20);
        const tail = await readView(size - tailLen, size);
        let eocd = -1;
        for (let i = tail.byteLength - 22; i >= 0; i--) {
            if (tail.getUint32(i, true) === 0x06054b50) { eocd = i; break; }
        }
        if (eocd < 0) return empty;

        let cdOffset = tail.getUint32(eocd + 16, true);
        let cdSize = tail.getUint32(eocd + 12, true);
        let entries = tail.getUint16(eocd + 10, true);

        // ZIP64 fallback for large archives (many region files).
        if (cdOffset === 0xffffffff || cdSize === 0xffffffff || entries === 0xffff) {
            const locPos = eocd - 20;
            if (locPos >= 0 && tail.getUint32(locPos, true) === 0x07064b50) {
                const z64Off = Number(tail.getBigUint64(locPos + 8, true));
                const z64 = await readView(z64Off, z64Off + 56);
                if (z64.getUint32(0, true) === 0x06064b50) {
                    cdSize = Number(z64.getBigUint64(40, true));
                    cdOffset = Number(z64.getBigUint64(48, true));
                }
            }
        }
        if (!cdSize || cdOffset + cdSize > size) return empty;

        const cd = await readView(cdOffset, cdOffset + cdSize);
        const decoder = new TextDecoder();
        const dims = {};
        const unknownDimensions = new Set();
        const customDimensions = {};
        let levelEntry = null;
        let p = 0;
        while (p + 46 <= cd.byteLength && cd.getUint32(p, true) === 0x02014b50) {
            const method = cd.getUint16(p + 10, true);
            let compSize = cd.getUint32(p + 20, true);
            const uncompRaw = cd.getUint32(p + 24, true);
            const nameLen = cd.getUint16(p + 28, true);
            const extraLen = cd.getUint16(p + 30, true);
            const commentLen = cd.getUint16(p + 32, true);
            let localOff = cd.getUint32(p + 42, true);
            const name = decoder
                .decode(new Uint8Array(cd.buffer, cd.byteOffset + p + 46, nameLen))
                .replace(/\\/g, '/');

            if (compSize === 0xffffffff || localOff === 0xffffffff) {
                let e = p + 46 + nameLen; const end = e + extraLen;
                while (e + 4 <= end) {
                    const id = cd.getUint16(e, true), sz = cd.getUint16(e + 2, true);
                    let q = e + 4;
                    if (id === 0x0001) {
                        if (uncompRaw === 0xffffffff) q += 8;
                        if (compSize === 0xffffffff) { compSize = Number(cd.getBigUint64(q, true)); q += 8; }
                        if (localOff === 0xffffffff) { localOff = Number(cd.getBigUint64(q, true)); }
                    }
                    e += 4 + sz;
                }
            }
            p += 46 + nameLen + extraLen + commentLen;

            const lower = name.toLowerCase();
            if (lower.endsWith('level.dat')) {
                const depth = name.split('/').length;
                if (!levelEntry || depth < levelEntry.depth) levelEntry = { method, compSize, localOff, depth };
                continue;
            }
            const segs = lower.split('/');
            const custom = customDimensionId(segs);
            const dim = custom ? null : classifyDimension(segs);
            if (!dim && !custom) continue;
            const parts = segs[segs.length - 1].split('.');
            let rx, rz;
            if ((lower.endsWith('.mca') || lower.endsWith('.mcr')) && segs.includes('region') &&
                parts.length === 4 && parts[0] === 'r') {
                rx = Number(parts[1]); rz = Number(parts[2]);
            } else if (lower.endsWith('.dat') && parts.length === 4 && parts[0] === 'c' &&
                segs.length >= 3 && segs[segs.length - 2].length <= 2 && segs[segs.length - 3].length <= 2) {
                const chunkX = parseBase36(parts[1]), chunkZ = parseBase36(parts[2]);
                if (!Number.isInteger(chunkX) || !Number.isInteger(chunkZ)) continue;
                rx = Math.floor(chunkX / 32); rz = Math.floor(chunkZ / 32);
            } else continue;
            if (!Number.isInteger(rx) || !Number.isInteger(rz)) continue;
            const target = custom ? customDimensions : dims;
            const key = custom || dim;
            (target[key] || (target[key] = [])).push([rx, rz]);
        }

        const customIds = Object.keys(customDimensions);
        if (customIds.length === 1) {
            const customId = customIds[0];
            const inferred = inferCustomDimension(customId);
            if (inferred && (!dims[inferred] || dims[inferred].length === 0))
                dims[inferred] = (dims[inferred] || []).concat(customDimensions[customId]);
            else
                unknownDimensions.add(customId);
        } else {
            for (const customId of customIds) unknownDimensions.add(customId);
        }

        const candidates = [];
        for (const dim of ['overworld', 'nether', 'end']) {
            const regions = dims[dim];
            if (!regions || regions.length === 0) continue;
            const c = densityCentroid(regions);
            candidates.push({ dimension: dim, centerX: c[0], centerZ: c[1] });
        }
        const lastPlayed = await readLastPlayed(file, levelEntry);
        return { candidates, unknownDimensions: Array.from(unknownDimensions).sort(), lastPlayed };
    } catch (e) {
        console.warn('atlasInspectWdl failed', e);
        return empty;
    }
};

function customDimensionId(segs) {
    const dimensions = segs.indexOf('dimensions');
    const region = segs.lastIndexOf('region');
    if (dimensions < 0 || region <= dimensions + 2) return null;
    const namespace = segs[dimensions + 1];
    const path = segs.slice(dimensions + 2, region).join('/');
    if (!namespace || !path || namespace === 'minecraft' &&
        ['overworld', 'the_nether', 'the_end'].includes(path)) return null;
    return namespace + ':' + path;
}

function inferCustomDimension(customId) {
    const tokens = customId.toLowerCase().split(/[:/._-]+/).filter(Boolean);
    if (tokens.includes('nether') || tokens.includes('hell')) return 'nether';
    if (tokens.includes('end')) return 'end';
    if (tokens.includes('overworld') || tokens.includes('surface')) return 'overworld';
    return null;
}

// Recognize canonical vanilla storage only. A museum's custom namespaced dimension is deliberately
// left unknown: its storage path cannot prove which dimension the build originally occupied on 2b2t.
function classifyDimension(segs) {
    if (segs.includes('dim-1')) return 'nether';
    if (segs.includes('dim1')) return 'end';
    const i = segs.indexOf('dimensions');
    if (i >= 0 && i + 2 < segs.length) {
        if (segs[i + 1] !== 'minecraft') return null;
        if (segs[i + 2] === 'overworld') return 'overworld';
        if (segs[i + 2] === 'the_nether') return 'nether';
        if (segs[i + 2] === 'the_end') return 'end';
        return null;
    }
    return 'overworld';
}

function parseBase36(value) {
    if (!/^-?[0-9a-z]+$/.test(value)) return NaN;
    const negative = value.startsWith('-');
    const parsed = parseInt(negative ? value.slice(1) : value, 36);
    return negative ? -parsed : parsed;
}

// Block centroid of the densest region cluster (D=2 neighborhood, C=8 cluster window), so a small
// spawn portion and a long highway trail don't skew the centroid away from the actual base.
function densityCentroid(regions) {
    const D = 2, C = 8;
    const present = new Set(regions.map(r => r[0] + ',' + r[1]));
    regions.sort((a, b) => a[0] - b[0] || a[1] - b[1]);

    let anchor = regions[0], best = -1;
    for (const [rx, rz] of regions) {
        let count = 0;
        for (let dx = -D; dx <= D; dx++)
            for (let dz = -D; dz <= D; dz++)
                if (present.has((rx + dx) + ',' + (rz + dz))) count++;
        if (count > best) { best = count; anchor = [rx, rz]; }
    }

    let sumX = 0, sumZ = 0, n = 0;
    for (const [rx, rz] of regions) {
        if (Math.abs(rx - anchor[0]) > C || Math.abs(rz - anchor[1]) > C) continue;
        sumX += rx * 512 + 256; sumZ += rz * 512 + 256; n++;
    }
    return [Math.trunc(sumX / n), Math.trunc(sumZ / n)];
}

async function inflate(bytes, format) {
    const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream(format));
    return new Uint8Array(await new Response(stream).arrayBuffer());
}

// Reads level.dat (deflated inside the ZIP -> gzip NBT) and returns its LastPlayed as yyyy-MM-dd.
async function readLastPlayed(file, entry) {
    try {
        if (!entry || typeof DecompressionStream === 'undefined') return null;
        const lh = new DataView(await file.slice(entry.localOff, entry.localOff + 30).arrayBuffer());
        const dataStart = entry.localOff + 30 + lh.getUint16(26, true) + lh.getUint16(28, true);
        const comp = new Uint8Array(await file.slice(dataStart, dataStart + entry.compSize).arrayBuffer());
        const zipContent = entry.method === 8 ? await inflate(comp, 'deflate-raw') : comp;
        let nbt;
        try { nbt = await inflate(zipContent, 'gzip'); } catch { nbt = zipContent; }

        // TAG_Long (0x04), name length 10, "LastPlayed", then an 8-byte big-endian epoch-ms value.
        const needle = [0x04, 0x00, 0x0a, 0x4c, 0x61, 0x73, 0x74, 0x50, 0x6c, 0x61, 0x79, 0x65, 0x64];
        for (let i = 0; i + needle.length + 8 <= nbt.length; i++) {
            let ok = true;
            for (let j = 0; j < needle.length; j++) if (nbt[i + j] !== needle[j]) { ok = false; break; }
            if (!ok) continue;
            const dv = new DataView(nbt.buffer, nbt.byteOffset + i + needle.length, 8);
            const ms = Number(dv.getBigInt64(0, false));
            const d = new Date(ms);
            if (ms > 0 && d.getUTCFullYear() >= 2010 &&
                d.getUTCFullYear() <= new Date().getUTCFullYear() + 1)
                return d.toISOString().slice(0, 10);
        }
        return null;
    } catch (e) {
        console.warn('readLastPlayed failed', e);
        return null;
    }
}
