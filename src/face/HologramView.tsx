import { useEffect, useRef } from 'react'
import * as THREE from 'three'
import { HoloMesh } from './HoloMesh'

export type MouthWeights = { jaw: number; funnel: number; wide: number }

type Props = {
  mouth: MouthWeights
  speaking?: boolean
  poseYaw?: number
  motionBoost?: number
}

/**
 * v0.1 / Android filamat hologram — fine scan, fresnel, vertex-color features, neck α.
 * Head mesh: lia_holo_mesh.bin (CPU jaw/funnel/wide) — proven lip morphs, no forehead junk.
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

  float scanWave = 0.5 + 0.5 * sin((vWorld.y * 155.0 + uTime * 1.7) * 3.14159265);
  float scan = pow(scanWave, 10.0) * 0.18;
  float speck = 0.028 * step(0.955, fract(vWorld.x * 72.0 + vWorld.y * 72.0 + vWorld.z * 24.0));

  float flick = 0.985 + 0.015 * sin(uTime * 21.0 + vWorld.y * 9.0);
  flick *= max(uGlow, 0.7);

  vec3 deep = vec3(0.012, 0.026, 0.055);
  vec3 cyan = vec3(0.18, 0.88, 1.0);
  vec3 soft = vec3(0.55, 0.82, 1.0);
  float fill = clamp(0.05 + fres * 1.0 + rim * 0.35, 0.0, 1.0);
  vec3 base = mix(deep, cyan, fill);
  base *= flick;
  base += cyan * (scan + speck);
  base += soft * rim * 0.28;
  float feat = max(0.0, vCol.g - fres * 0.85);
  base += cyan * feat * 0.5;

  // Soft neck → transparent (no black blob)
  float neck = smoothstep(-0.50, -0.05, vWorld.y);
  float alpha = clamp(neck * (0.38 + fill * 0.58), 0.0, 0.95);
  base *= mix(0.55, 1.0, neck);

  gl_FragColor = vec4(base, alpha);
}
`

/** Almond oral cavity at the LIPS (same as Android buildLensCard). */
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

function makeRing(radius: number, tube: number, color: number, opacity: number, arc = Math.PI * 2) {
  const geo = new THREE.TorusGeometry(radius, tube, 8, 100, arc)
  const mat = new THREE.MeshBasicMaterial({
    color,
    transparent: true,
    opacity,
    depthTest: true,
    depthWrite: false,
    side: THREE.DoubleSide,
  })
  const m = new THREE.Mesh(geo, mat)
  m.renderOrder = 0
  m.frustumCulled = false
  return m
}

export function HologramView({ mouth, speaking, poseYaw = 0, motionBoost = 1 }: Props) {
  const mountRef = useRef<HTMLDivElement>(null)
  const mouthRef = useRef(mouth)
  const speakingRef = useRef(!!speaking)
  const poseYawRef = useRef(poseYaw)
  const boostRef = useRef(motionBoost)
  mouthRef.current = mouth
  speakingRef.current = !!speaking
  poseYawRef.current = poseYaw
  boostRef.current = motionBoost

  useEffect(() => {
    const el = mountRef.current
    if (!el) return

    const scene = new THREE.Scene()
    scene.background = null

    // Frontal framed bust with margin — ~65–70% of panel
    const camera = new THREE.PerspectiveCamera(28, 1, 0.05, 40)
    camera.position.set(0, -0.05, 2.95)
    camera.lookAt(0, -0.08, 0)

    let renderer: THREE.WebGLRenderer
    try {
      renderer = new THREE.WebGLRenderer({
        antialias: true,
        alpha: true,
        premultipliedAlpha: false,
        powerPreference: 'high-performance',
      })
    } catch (err) {
      console.error('WebGL unavailable', err)
      el.innerHTML =
        '<div style="display:flex;align-items:center;justify-content:center;height:100%;color:#5AD6FF">ЛИА · 3D</div>'
      return
    }
    renderer.setClearColor(0x000000, 0)
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
      transparent: true,
      depthWrite: true,
      side: THREE.DoubleSide,
    })

    const root = new THREE.Group()
    root.renderOrder = 2
    scene.add(root)

    // ——— HUD rings BEHIND the head plane (never through the face) ———
    const hud = new THREE.Group()
    // Entire ring stack sits behind the bust
    hud.position.set(0, -0.05, -1.05)
    hud.renderOrder = 0
    scene.add(hud)

    const amber = 0xff9a3c
    const cyan = 0x5ad6ff

    // Mild tilt only; large radius; all geometry z ≤ −0.85 in world via hud parent
    const ringA = makeRing(1.65, 0.006, amber, 0.4)
    ringA.rotation.x = 0.32
    ringA.position.z = 0.05
    const ringB = makeRing(1.85, 0.005, cyan, 0.32)
    ringB.rotation.x = -0.28
    ringB.rotation.y = 0.15
    ringB.position.z = -0.12
    const ringC = makeRing(2.05, 0.004, amber, 0.22)
    ringC.rotation.x = 0.4
    ringC.rotation.z = 0.2
    ringC.position.z = -0.25

    const arc1 = makeRing(1.7, 0.009, amber, 0.48, Math.PI * 0.5)
    arc1.rotation.set(0.35, 0.1, 0.85)
    arc1.position.z = 0.0
    const arc2 = makeRing(1.95, 0.007, cyan, 0.38, Math.PI * 0.4)
    arc2.rotation.set(-0.3, -0.12, -1.05)
    arc2.position.z = -0.18

    const waveGroup = new THREE.Group()
    waveGroup.position.z = -0.08
    waveGroup.rotation.x = 0.3
    const waveBars: THREE.Mesh[] = []
    for (let i = 0; i < 40; i++) {
      const a = (i / 40) * Math.PI * 2
      const bar = new THREE.Mesh(
        new THREE.BoxGeometry(0.012, 0.05, 0.012),
        new THREE.MeshBasicMaterial({
          color: i % 2 === 0 ? amber : cyan,
          transparent: true,
          opacity: 0.35,
          depthTest: true,
          depthWrite: false,
        }),
      )
      bar.position.set(Math.cos(a) * 1.5, Math.sin(a) * 1.5, 0)
      bar.rotation.z = a
      bar.renderOrder = 0
      waveGroup.add(bar)
      waveBars.push(bar)
    }

    hud.add(ringA, ringB, ringC, arc1, arc2, waveGroup)

    let mesh: THREE.Mesh | null = null
    let holo: HoloMesh | null = null
    let posAttr: THREE.BufferAttribute | null = null
    let cavity: THREE.Mesh | null = null
    const scratch = { arr: null as Float32Array | null }

    let yaw = 0
    let pitch = 0
    let targetYaw = 0
    let targetPitch = 0
    let dragging = false
    let lastX = 0
    let lastY = 0
    let ptrX = 0
    let ptrY = 0

    const onDown = (e: PointerEvent) => {
      dragging = true
      lastX = e.clientX
      lastY = e.clientY
      el.setPointerCapture(e.pointerId)
    }
    const onMove = (e: PointerEvent) => {
      const rect = el.getBoundingClientRect()
      ptrX = ((e.clientX - rect.left) / rect.width) * 2 - 1
      ptrY = -(((e.clientY - rect.top) / rect.height) * 2 - 1)
      if (!dragging) return
      targetYaw += (e.clientX - lastX) * 0.004
      targetPitch += (e.clientY - lastY) * 0.0028
      targetYaw = Math.max(-0.85, Math.min(0.85, targetYaw))
      targetPitch = Math.max(-0.35, Math.min(0.35, targetPitch))
      lastX = e.clientX
      lastY = e.clientY
    }
    const onUp = () => {
      dragging = false
      targetYaw = poseYawRef.current || 0
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

    ;(async () => {
      try {
        try {
          if (window.lia?.meshBytes) {
            const bytes = await window.lia.meshBytes()
            holo = HoloMesh.fromBytes(bytes)
          }
        } catch (e) {
          console.warn('meshBytes', e)
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
        mesh.renderOrder = 3
        // ~65–70% of panel with margin (not overflowing)
        mesh.scale.setScalar(0.58)
        mesh.position.set(0, -0.11, 0)
        mesh.rotation.set(0, 0, 0)
        root.add(mesh)

        // Lip cavity only — anchored to HoloMesh lip landmarks
        const cavityMat = new THREE.MeshBasicMaterial({
          color: new THREE.Color(0.008, 0.02, 0.035),
          transparent: true,
          opacity: 0.94,
          depthWrite: true,
          side: THREE.DoubleSide,
        })
        cavity = new THREE.Mesh(buildLensGeo(holo.mouthHalfW * 0.7, 0.012, 0.001), cavityMat)
        cavity.visible = false
        cavity.position.set(0, holo.lipY, holo.mouthZ - 0.03)
        cavity.renderOrder = 4
        mesh.add(cavity)
      } catch (err) {
        console.error('holo load', err)
      }
    })()

    let raf = 0
    const t0 = performance.now()
    const DEG = Math.PI / 180
    const IDLE_YAW = 8 * DEG
    const IDLE_PITCH = 4 * DEG

    const tick = () => {
      raf = requestAnimationFrame(tick)
      const t = (performance.now() - t0) / 1000
      const boost = Math.min(boostRef.current, 1.25)

      material.uniforms.uTime.value = t
      material.uniforms.uGlow.value = speakingRef.current ? 1.12 : 1.0
      material.uniforms.uCam.value.copy(camera.position)

      if (!dragging) {
        const pose = poseYawRef.current
        const hold = Math.abs(pose) > 0.1
        targetYaw = pose + (hold ? 0 : Math.sin(t * 0.32) * IDLE_YAW * boost)
        targetPitch = hold ? 0 : Math.sin(t * 0.27) * IDLE_PITCH * boost + Math.sin(t * 1.1) * 0.006
      }

      const holdPose = Math.abs(poseYawRef.current) > 0.1
      const follow = dragging ? 0.22 : holdPose ? 0.32 : 0.1
      yaw += (targetYaw - yaw) * follow
      pitch += (targetPitch - pitch) * follow
      root.rotation.y = yaw
      root.rotation.x = pitch

      // Stable camera — slight parallax only
      camera.position.x += (ptrX * 0.06 - camera.position.x) * 0.05
      camera.position.y += (-0.05 + ptrY * 0.04 - camera.position.y) * 0.05
      camera.position.z = 2.95
      camera.lookAt(0, -0.08, 0)

      ringA.rotation.z = t * 0.2
      ringB.rotation.z = -t * 0.14
      ringC.rotation.z = t * 0.09
      arc1.rotation.z = t * 0.3 + 0.85
      arc2.rotation.z = -t * 0.24 - 1.05
      waveGroup.rotation.z = t * 0.12
      hud.position.x = ptrX * 0.025

      if (holo && posAttr && scratch.arr && mesh) {
        const m = mouthRef.current
        holo.morphedPositions(m.jaw, m.funnel, m.wide, scratch.arr)
        posAttr.needsUpdate = true

        if (cavity) {
          const open = m.jaw > 0.28
          cavity.visible = open
          if (open) {
            const sx = Math.min(0.95, Math.max(0.45, 0.78 - 0.35 * m.funnel + 0.18 * m.wide))
            const sy = Math.min(0.95, Math.max(0.3, 0.35 + 0.75 * m.jaw))
            cavity.scale.set(sx, sy, 1)
            cavity.position.set(0, holo.lipY - 0.003 - 0.012 * m.jaw, holo.mouthZ - 0.03)
          }
        }
      }

      const speak = speakingRef.current
      const jaw = mouthRef.current.jaw
      for (let i = 0; i < waveBars.length; i++) {
        const phase = t * (speak ? 8 : 2) + i * 0.35
        const base = speak ? 0.28 + jaw * 0.75 : 0.1
        waveBars[i].scale.y = Math.max(0.12, base * (0.45 + 0.55 * Math.abs(Math.sin(phase))) * 3.5)
        ;(waveBars[i].material as THREE.MeshBasicMaterial).opacity = speak ? 0.5 : 0.25
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

  useEffect(() => {
    poseYawRef.current = poseYaw
  }, [poseYaw])
  useEffect(() => {
    boostRef.current = motionBoost
  }, [motionBoost])

  return <div className="holo-stage" ref={mountRef} data-testid="holo-stage" />
}
