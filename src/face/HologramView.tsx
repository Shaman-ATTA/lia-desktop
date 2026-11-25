import { useEffect, useRef } from 'react'
import * as THREE from 'three'
import { HoloMesh } from './HoloMesh'

/**
 * Port of Android lia_holo.filamat look:
 * thin scan = pow(0.5+0.5*sin((y*155+t*1.7)*π), 10)*0.18
 * flicker = 0.985+0.015*sin(t*21+y*9)
 * vertexColor.g boosts eyes/lips/brows
 */
const VERT = /* glsl */ `
varying vec3 vWorld;
varying vec3 vN;
varying vec4 vCol;
void main(){
  vCol = color;
  vec4 wp = modelMatrix * vec4(position,1.0);
  vWorld = wp.xyz;
  vN = normalize(mat3(modelMatrix) * normal);
  gl_Position = projectionMatrix * viewMatrix * wp;
}
`

const FRAG = /* glsl */ `
precision highp float;
uniform float uTime;
uniform float uGlow;
uniform vec3 uCam;
varying vec3 vWorld;
varying vec3 vN;
varying vec4 vCol;

void main(){
  vec3 N = normalize(vN);
  vec3 V = normalize(uCam - vWorld);
  float ndv = max(dot(N, V), 0.0);
  float fres = pow(1.0 - ndv, 2.4);
  float rim = pow(1.0 - ndv, 3.2);

  // Thin fine scanlines (Android filamat: y*155, pow 10)
  float scanWave = 0.5 + 0.5 * sin((vWorld.y * 155.0 + uTime * 1.7) * 3.14159265);
  float scan = pow(scanWave, 10.0) * 0.18;
  // Micro grid speck (very subtle)
  float speck = 0.028 * step(0.955, fract(vWorld.x * 72.0 + vWorld.y * 72.0 + vWorld.z * 24.0));

  float flick = 0.985 + 0.015 * sin(uTime * 21.0 + vWorld.y * 9.0);
  flick *= max(uGlow, 0.7);

  vec3 deep = vec3(0.012, 0.026, 0.055);
  vec3 cyan = vec3(0.18, 0.88, 1.0);
  vec3 mag = vec3(0.70, 0.42, 1.0);
  float fill = clamp(0.05 + fres * 1.0 + rim * 0.35, 0.0, 1.0);
  vec3 base = mix(deep, cyan, fill);
  base *= flick;
  base += cyan * (scan + speck);
  base += mag * rim * 0.28;
  // Feature lift from baked vertex color (eyes / brows / lips)
  float feat = max(0.0, vCol.g - fres * 0.85);
  base += cyan * feat * 0.45;

  // Neck fade (Android soft bottom)
  float neck = smoothstep(-0.48, -0.08, vWorld.y);
  base = max(base, deep) * neck;

  gl_FragColor = vec4(base, 1.0);
}
`

export type MouthWeights = { jaw: number; funnel: number; wide: number }

type Props = {
  mouth: MouthWeights
  speaking?: boolean
}

function buildLensGeo(rx: number, ry: number, z: number): THREE.BufferGeometry {
  const segments = 40
  const nPow = 1.55
  const lensXY = (a: number, sx: number, sy: number) => {
    const c = Math.cos(a)
    const s = Math.sin(a)
    const x = Math.sign(c) * sx * Math.pow(Math.abs(c), 2 / nPow)
    const y = Math.sign(s) * sy * Math.pow(Math.abs(s), 2 / nPow)
    return [x, y] as const
  }
  const positions: number[] = [0, 0, z]
  for (let i = 0; i < segments; i++) {
    const a = (i / segments) * Math.PI * 2
    const [x, y] = lensXY(a, rx, ry)
    positions.push(x, y, z)
  }
  const indices: number[] = []
  for (let i = 0; i < segments; i++) {
    indices.push(0, 1 + i, 1 + ((i + 1) % segments))
  }
  const geo = new THREE.BufferGeometry()
  geo.setAttribute('position', new THREE.Float32BufferAttribute(positions, 3))
  geo.setIndex(indices)
  return geo
}

export function HologramView({ mouth, speaking }: Props) {
  const mountRef = useRef<HTMLDivElement>(null)
  const mouthRef = useRef(mouth)
  const speakingRef = useRef(!!speaking)
  mouthRef.current = mouth
  speakingRef.current = !!speaking

  useEffect(() => {
    const el = mountRef.current
    if (!el) return

    const scene = new THREE.Scene()
    scene.background = new THREE.Color(0x0a0714)

    // Match Android SceneView framing
    const camera = new THREE.PerspectiveCamera(28, 1, 0.05, 20)
    camera.position.set(0, -0.02, 3.4)
    camera.lookAt(0, -0.02, 0)

    let renderer: THREE.WebGLRenderer
    try {
      renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false, powerPreference: 'high-performance' })
    } catch (err) {
      console.error('WebGL unavailable', err)
      el.innerHTML =
        '<div style="display:flex;align-items:center;justify-content:center;height:100%;color:#6EE7FF;letter-spacing:.12em">ЛИА · WebGL</div>'
      return
    }
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    el.appendChild(renderer.domElement)

    const material = new THREE.ShaderMaterial({
      vertexShader: VERT,
      fragmentShader: FRAG,
      uniforms: {
        uTime: { value: 0 },
        uGlow: { value: 1 },
        uCam: { value: camera.position.clone() },
      },
      vertexColors: true,
      side: THREE.DoubleSide,
    })

    let mesh: THREE.Mesh | null = null
    let holo: HoloMesh | null = null
    let posAttr: THREE.BufferAttribute | null = null
    let cavity: THREE.Mesh | null = null
    const scratch = { arr: null as Float32Array | null }

    // Drag → spring back to frontal
    let yaw = 0
    let pitch = 0
    let targetYaw = 0
    let targetPitch = 0
    let dragging = false
    let lastX = 0
    let lastY = 0

    const onDown = (e: PointerEvent) => {
      dragging = true
      lastX = e.clientX
      lastY = e.clientY
      el.setPointerCapture(e.pointerId)
    }
    const onMove = (e: PointerEvent) => {
      if (!dragging) return
      targetYaw += (e.clientX - lastX) * 0.0035
      targetPitch += (e.clientY - lastY) * 0.0025
      targetYaw = Math.max(-0.55, Math.min(0.55, targetYaw))
      targetPitch = Math.max(-0.22, Math.min(0.22, targetPitch))
      lastX = e.clientX
      lastY = e.clientY
    }
    const onUp = () => {
      dragging = false
      // Return to frontal
      targetYaw = 0
      targetPitch = 0
    }
    el.addEventListener('pointerdown', onDown)
    el.addEventListener('pointermove', onMove)
    el.addEventListener('pointerup', onUp)
    el.addEventListener('pointercancel', onUp)

    const resize = () => {
      const w = el.clientWidth
      const h = el.clientHeight
      camera.aspect = w / Math.max(1, h)
      camera.updateProjectionMatrix()
      renderer.setSize(w, h, false)
    }
    resize()
    const ro = new ResizeObserver(resize)
    ro.observe(el)

    let raf = 0
    const t0 = performance.now()

    ;(async () => {
      try {
        // Prefer IPC bytes (works in packaged Electron); fall back to fetch.
        try {
          if (window.lia?.meshBytes) {
            const bytes = await window.lia.meshBytes()
            holo = HoloMesh.fromBytes(bytes)
          }
        } catch (e) {
          console.warn('meshBytes failed, trying fetch', e)
        }
        if (!holo) {
          const candidates = [
            new URL('models/lia_holo_mesh.bin', document.baseURI).href,
            './models/lia_holo_mesh.bin',
            'models/lia_holo_mesh.bin',
          ]
          let lastErr: unknown
          for (const url of candidates) {
            try {
              holo = await HoloMesh.load(url)
              lastErr = null
              break
            } catch (e) {
              lastErr = e
            }
          }
          if (!holo) throw lastErr ?? new Error('mesh load failed')
        }
        const geo = new THREE.BufferGeometry()
        scratch.arr = new Float32Array(holo.basePositions)
        geo.setAttribute('position', new THREE.BufferAttribute(scratch.arr, 3))
        geo.setAttribute('normal', new THREE.BufferAttribute(holo.normals.slice(), 3))
        geo.setAttribute('color', new THREE.BufferAttribute(holo.colors, 4))
        geo.setIndex(new THREE.BufferAttribute(holo.indices, 1))
        geo.computeVertexNormals()
        posAttr = geo.getAttribute('position') as THREE.BufferAttribute

        mesh = new THREE.Mesh(geo, material)
        // Android: scale 0.50, position y=-0.10 — frontal (no baked 3/4)
        mesh.scale.setScalar(0.5)
        mesh.position.set(0, -0.1, 0)
        mesh.rotation.set(0, 0, 0)
        scene.add(mesh)

        // Almond lens cavity (same idea as Android buildLensCard)
        const lensMat = new THREE.MeshBasicMaterial({
          color: new THREE.Color(0.01, 0.004, 0.016),
          transparent: true,
          opacity: 0.92,
          depthWrite: true,
          side: THREE.DoubleSide,
        })
        cavity = new THREE.Mesh(
          buildLensGeo(holo.mouthHalfW * 0.7, 0.012, 0.001),
          lensMat,
        )
        cavity.visible = false
        cavity.position.set(0, holo.lipY, holo.mouthZ - 0.03)
        mesh.add(cavity)
      } catch (err) {
        console.error('holo load', err)
      }
    })()

    const tick = () => {
      raf = requestAnimationFrame(tick)
      const t = (performance.now() - t0) / 1000
      material.uniforms.uTime.value = t
      material.uniforms.uGlow.value = speakingRef.current ? 1.12 : 1.0
      material.uniforms.uCam.value.copy(camera.position)

      const follow = dragging ? 0.18 : 0.1
      yaw += (targetYaw - yaw) * follow
      pitch += (targetPitch - pitch) * follow
      if (mesh) {
        mesh.rotation.y = yaw
        mesh.rotation.x = pitch
      }

      if (holo && posAttr && scratch.arr && mesh) {
        const m = mouthRef.current
        holo.morphedPositions(m.jaw, m.funnel, m.wide, scratch.arr)
        posAttr.needsUpdate = true

        if (cavity) {
          const open = m.jaw > 0.3
          cavity.visible = open
          if (open) {
            const sx = Math.min(0.95, Math.max(0.45, 0.78 - 0.35 * m.funnel + 0.18 * m.wide))
            const sy = Math.min(0.95, Math.max(0.3, 0.35 + 0.75 * m.jaw))
            cavity.scale.set(sx, sy, 1)
            cavity.position.set(0, holo.lipY - 0.003 - 0.012 * m.jaw, holo.mouthZ - 0.03)
          }
        }
      }
      renderer.render(scene, camera)
    }
    tick()

    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
      el.removeEventListener('pointerdown', onDown)
      el.removeEventListener('pointermove', onMove)
      el.removeEventListener('pointerup', onUp)
      el.removeEventListener('pointercancel', onUp)
      renderer.dispose()
      material.dispose()
      if (renderer.domElement.parentElement === el) el.removeChild(renderer.domElement)
    }
  }, [])

  return <div className="holo-stage" ref={mountRef} />
}
