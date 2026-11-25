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
 * Clean frontal = opaque FrontSide + baked normals (never recompute) + no cavity.
 * computeVertexNormals / log-depth / face alpha all caused speckles or killed rings.
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
  if (!gl_FrontFacing) N = -N;
  vec3 V = normalize(uCam - vWorld);
  float ndv = max(dot(N, V), 0.0);
  float fres = pow(1.0 - ndv, 2.4);
  float rim = pow(1.0 - ndv, 3.2);

  float scanWave = 0.5 + 0.5 * sin((vWorld.y * 155.0 + uTime * 1.7) * 3.14159265);
  float scan = pow(scanWave, 10.0) * 0.18;

  float flick = 0.988 + 0.012 * sin(uTime * 21.0 + vWorld.y * 9.0);
  flick *= max(uGlow, 0.75);

  vec3 deep = vec3(0.02, 0.05, 0.09);
  vec3 cyan = vec3(0.2, 0.9, 1.0);
  vec3 soft = vec3(0.55, 0.85, 1.0);
  float fill = clamp(0.14 + fres * 0.85 + rim * 0.28, 0.0, 1.0);
  vec3 base = mix(deep, cyan, fill);
  base *= flick;
  base += cyan * scan;
  base += soft * rim * 0.32;
  float feat = max(0.0, vCol.g - fres * 0.7);
  base += cyan * feat * 0.55;

  // Opaque face; neck darkens into bg (no alpha holes)
  float neck = smoothstep(-0.95, -0.58, vWorld.y);
  base *= neck;
  gl_FragColor = vec4(base, 1.0);
}
`

function makeRing(radius: number, tube: number, color: number, opacity: number, arc = Math.PI * 2) {
  const m = new THREE.Mesh(
    new THREE.TorusGeometry(radius, tube, 10, 128, arc),
    new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity,
      depthTest: true,
      depthWrite: false,
      side: THREE.DoubleSide,
    }),
  )
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

    const camera = new THREE.PerspectiveCamera(28, 1, 0.05, 40)
    // ~65% panel: scale 0.68 @ z=2.55
    camera.position.set(0, -0.05, 2.55)
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
    renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))
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
      transparent: false,
      depthWrite: true,
      depthTest: true,
      side: THREE.FrontSide,
    })

    const root = new THREE.Group()
    root.renderOrder = 2
    scene.add(root)

    // Rings behind face — tilt mild so torus front stays rear of chin
    const hud = new THREE.Group()
    hud.position.set(0, -0.04, -0.7)
    scene.add(hud)

    const amber = 0xff9a3c
    const cyan = 0x5ad6ff

    const ringA = makeRing(0.86, 0.012, amber, 0.82)
    ringA.rotation.x = 0.2
    const ringB = makeRing(1.0, 0.009, cyan, 0.64)
    ringB.rotation.x = -0.16
    ringB.rotation.y = 0.12
    ringB.position.z = -0.06
    const ringC = makeRing(1.14, 0.007, amber, 0.48)
    ringC.rotation.x = 0.24
    ringC.rotation.z = 0.14
    ringC.position.z = -0.12

    const arc1 = makeRing(0.9, 0.015, amber, 0.88, Math.PI * 0.55)
    arc1.rotation.set(0.2, 0.06, 0.7)
    const arc2 = makeRing(1.04, 0.012, cyan, 0.72, Math.PI * 0.45)
    arc2.rotation.set(-0.16, -0.08, -1.0)
    arc2.position.z = -0.05
    const arc3 = makeRing(1.18, 0.01, amber, 0.55, Math.PI * 0.4)
    arc3.rotation.set(0.26, 0.04, 2.1)
    arc3.position.z = -0.1

    const ticks = new THREE.Group()
    ticks.rotation.x = 0.2
    for (let i = 0; i < 40; i++) {
      const a = (i / 40) * Math.PI * 2
      const len = i % 5 === 0 ? 0.045 : 0.022
      const tick = new THREE.Mesh(
        new THREE.BoxGeometry(0.006, len, 0.006),
        new THREE.MeshBasicMaterial({
          color: amber,
          transparent: true,
          opacity: i % 5 === 0 ? 0.85 : 0.42,
          depthTest: true,
          depthWrite: false,
        }),
      )
      tick.position.set(Math.cos(a) * 0.86, Math.sin(a) * 0.86, 0)
      tick.rotation.z = a
      ticks.add(tick)
    }

    const waveGroup = new THREE.Group()
    waveGroup.rotation.x = 0.2
    waveGroup.position.z = 0.02
    const waveBars: THREE.Mesh[] = []
    for (let i = 0; i < 48; i++) {
      const a = (i / 48) * Math.PI * 2
      const bar = new THREE.Mesh(
        new THREE.BoxGeometry(0.014, 0.055, 0.014),
        new THREE.MeshBasicMaterial({
          color: i % 2 === 0 ? amber : cyan,
          transparent: true,
          opacity: 0.55,
          depthTest: true,
          depthWrite: false,
        }),
      )
      bar.position.set(Math.cos(a) * 0.78, Math.sin(a) * 0.78, 0)
      bar.rotation.z = a
      waveGroup.add(bar)
      waveBars.push(bar)
    }

    hud.add(ringA, ringB, ringC, arc1, arc2, arc3, ticks, waveGroup)

    let mesh: THREE.Mesh | null = null
    let holo: HoloMesh | null = null
    let posAttr: THREE.BufferAttribute | null = null
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
            holo = HoloMesh.fromBytes(await window.lia.meshBytes())
          }
        } catch (e) {
          console.warn('meshBytes', e)
        }
        if (!holo) {
          const urls = [
            new URL('models/lia_holo_mesh.bin', document.baseURI).href,
            './models/lia_holo_mesh.bin',
            'models/lia_holo_mesh.bin',
          ]
          let lastErr: unknown
          for (const url of urls) {
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
        // Baked normals only — never computeVertexNormals (corrupts frontal)
        geo.setAttribute('normal', new THREE.BufferAttribute(holo.normals.slice(), 3))
        geo.setAttribute('color', new THREE.BufferAttribute(holo.colors, 4))
        geo.setIndex(new THREE.BufferAttribute(holo.indices, 1))
        posAttr = geo.getAttribute('position') as THREE.BufferAttribute

        mesh = new THREE.Mesh(geo, material)
        mesh.renderOrder = 3
        mesh.scale.setScalar(0.68)
        mesh.position.set(0, -0.08, 0)
        root.add(mesh)
      } catch (err) {
        console.error('holo load', err)
      }
    })()

    let raf = 0
    const t0 = performance.now()
    const DEG = Math.PI / 180

    const tick = () => {
      raf = requestAnimationFrame(tick)
      const t = (performance.now() - t0) / 1000
      const boost = Math.min(boostRef.current, 1.2)

      material.uniforms.uTime.value = t
      material.uniforms.uGlow.value = speakingRef.current ? 1.1 : 1.0
      material.uniforms.uCam.value.copy(camera.position)

      if (!dragging) {
        const pose = poseYawRef.current
        const hold = Math.abs(pose) > 0.1
        targetYaw = pose + (hold ? 0 : Math.sin(t * 0.32) * 8 * DEG * boost)
        targetPitch = hold ? 0 : Math.sin(t * 0.27) * 4 * DEG * boost
      }

      const holdPose = Math.abs(poseYawRef.current) > 0.1
      const follow = dragging ? 0.22 : holdPose ? 0.32 : 0.1
      yaw += (targetYaw - yaw) * follow
      pitch += (targetPitch - pitch) * follow
      root.rotation.y = yaw
      root.rotation.x = pitch

      camera.position.x += (ptrX * 0.04 - camera.position.x) * 0.05
      camera.position.y += (-0.05 + ptrY * 0.03 - camera.position.y) * 0.05
      camera.position.z = 2.55
      camera.lookAt(0, -0.08, 0)

      ringA.rotation.z = t * 0.2
      ringB.rotation.z = -t * 0.14
      ringC.rotation.z = t * 0.09
      arc1.rotation.z = t * 0.28 + 0.75
      arc2.rotation.z = -t * 0.22 - 1.0
      arc3.rotation.z = t * 0.16 + 2.15
      ticks.rotation.z = t * 0.2
      waveGroup.rotation.z = t * 0.2

      if (holo && posAttr && scratch.arr && mesh) {
        const m = mouthRef.current
        // Soft jaw — hard opens self-intersect on SwiftShader frontal; A still readable
        const jawVis = Math.min(0.72, Math.max(0, m.jaw * 0.92))
        holo.morphedPositions(jawVis, m.funnel * 0.55, m.wide * 0.55, scratch.arr)
        posAttr.needsUpdate = true
      }

      const speak = speakingRef.current
      const jaw = mouthRef.current.jaw
      for (let i = 0; i < waveBars.length; i++) {
        const phase = t * (speak ? 9 : 2.2) + i * 0.38
        const base = speak ? 0.35 + jaw * 0.9 : 0.14
        waveBars[i].scale.y = Math.max(0.15, base * (0.45 + 0.55 * Math.abs(Math.sin(phase))) * 4)
        ;(waveBars[i].material as THREE.MeshBasicMaterial).opacity = speak ? 0.75 : 0.42
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
