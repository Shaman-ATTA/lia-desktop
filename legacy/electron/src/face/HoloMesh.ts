/** Port of Android HoloMesh — loads lia_holo_mesh.bin for CPU jaw/funnel/wide morphs. */

export class HoloMesh {
  basePositions: Float32Array
  jawDeltas: Float32Array
  funnelDeltas: Float32Array
  wideDeltas: Float32Array
  normals: Float32Array
  uvs: Float32Array
  colors: Float32Array
  indices: Uint32Array
  vertexCount: number
  lipY: number
  mouthHalfW: number
  mouthZ: number

  private constructor(init: {
    basePositions: Float32Array
    jawDeltas: Float32Array
    funnelDeltas: Float32Array
    wideDeltas: Float32Array
    normals: Float32Array
    uvs: Float32Array
    colors: Float32Array
    indices: Uint32Array
    vertexCount: number
    lipY: number
    mouthHalfW: number
    mouthZ: number
  }) {
    Object.assign(this, init)
  }

  morphedPositions(jaw: number, funnel = 0, wide = 0, out?: Float32Array): Float32Array {
    const j = Math.min(1.05, Math.max(0, jaw))
    const f = Math.min(1, Math.max(0, funnel))
    const w = Math.min(1, Math.max(0, wide))
    const dest = out ?? new Float32Array(this.basePositions.length)
    const b = this.basePositions
    const jd = this.jawDeltas
    const fd = this.funnelDeltas
    const wd = this.wideDeltas
    for (let i = 0; i < b.length; i++) {
      dest[i] = b[i] + jd[i] * j + fd[i] * f + wd[i] * w
    }
    return dest
  }

  static fromArrayBuffer(buf: ArrayBuffer): HoloMesh {
    const view = new DataView(buf)
    let o = 0
    const metaLen = view.getInt32(o, true)
    o += 4
    const metaBytes = new Uint8Array(buf, o, metaLen)
    o += metaLen
    const meta = JSON.parse(new TextDecoder().decode(metaBytes)) as {
      vertexCount: number
      indexCount: number
      has_extra_morphs?: boolean
      lip_y?: number
      mouth_L?: number[]
      mouth_R?: number[]
      mouth_center?: number[]
    }
    const n = meta.vertexCount
    const indexCount = meta.indexCount
    const hasExtra = !!meta.has_extra_morphs

    const readFloats = (count: number) => {
      const a = new Float32Array(count)
      for (let i = 0; i < count; i++) {
        a[i] = view.getFloat32(o, true)
        o += 4
      }
      return a
    }
    const pos = readFloats(n * 3)
    const nrm = readFloats(n * 3)
    const uv = readFloats(n * 2)
    const col = readFloats(n * 4)
    const jaw = readFloats(n * 3)
    const funnel = hasExtra ? readFloats(n * 3) : new Float32Array(n * 3)
    const wide = hasExtra ? readFloats(n * 3) : new Float32Array(n * 3)
    const indices = new Uint32Array(indexCount)
    for (let i = 0; i < indexCount; i++) {
      indices[i] = view.getInt32(o, true)
      o += 4
    }

    let halfW = 0.09
    if (meta.mouth_L && meta.mouth_R) {
      halfW = Math.min(0.14, Math.max(0.05, 0.5 * Math.abs(meta.mouth_R[0] - meta.mouth_L[0])))
    }
    const mouthZ = meta.mouth_center?.[2] ?? meta.mouth_L?.[2] ?? 0.5

    return new HoloMesh({
      basePositions: pos,
      jawDeltas: jaw,
      funnelDeltas: funnel,
      wideDeltas: wide,
      normals: nrm,
      uvs: uv,
      colors: col,
      indices,
      vertexCount: n,
      lipY: meta.lip_y ?? 0.072,
      mouthHalfW: halfW,
      mouthZ,
    })
  }

  static async load(url: string): Promise<HoloMesh> {
    const res = await fetch(url)
    if (!res.ok) throw new Error(`mesh fetch ${res.status} ${url}`)
    return HoloMesh.fromArrayBuffer(await res.arrayBuffer())
  }

  static fromBytes(bytes: ArrayBuffer | Uint8Array): HoloMesh {
    const buf =
      bytes instanceof ArrayBuffer
        ? bytes
        : bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength)
    return HoloMesh.fromArrayBuffer(buf as ArrayBuffer)
  }
}
