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
 * Opaque FrontSide + baked normals. Finer scan, eye glow, blink/breathe.
 * Rings: thick torus + lights, deep tilt, particles, camera orbit/parallax.
 */
const VERT = /* glsl */ `
varying vec3 vWorld;
varying vec3 vN;
varying vec4 vCol;
varying vec3 vLocal;
void main(){
  vCol = color;
  vLocal = position;
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
uniform float uBlink;
uniform vec3 uCam;
varying vec3 vWorld;
varying vec3 vN;
varying vec4 vCol;
varying vec3 vLocal;

void main(){
  vec3 N = normalize(vN);
  if (!gl_FrontFacing) N = -N;
  vec3 V = normalize(uCam - vWorld);
  float ndv = max(dot(N, V), 0.0);
  float fres = pow(1.0 - ndv, 2.15);
  float rim = pow(1.0 - ndv, 3.6);

  // Finer scanlines (was y*155 / pow10 → coarse mannequin bars)
  float scanWave = 0.5 + 0.5 * sin((vWorld.y * 280.0 + uTime * 1.35) * 3.14159265);
  float scan = pow(scanWave, 7.0) * 0.14;
  float scanFine = pow(0.5 + 0.5 * sin((vWorld.y * 520.0 - uTime * 0.9) * 3.14159265), 12.0) * 0.05;

  float flick = 0.992 + 0.008 * sin(uTime * 18.0 + vWorld.y * 7.0);
  flick *= max(uGlow, 0.78);

  vec3 deep = vec3(0.018, 0.045, 0.085);
  vec3 cyan = vec3(0.22, 0.92, 1.0);
  vec3 soft = vec3(0.62, 0.88, 1.0);
  float fill = clamp(0.16 + fres * 0.82 + rim * 0.3, 0.0, 1.0);
  vec3 base = mix(deep, cyan, fill);
  base *= flick;
  base += cyan * (scan + scanFine);
  // Stronger feminine rim
  base += soft * rim * 0.48;

  // Feature lift from vertex colors (brows/lips)
  float feat = max(0.0, vCol.g - fres * 0.55);
  base += cyan * feat * 0.7;

  // Eye iris / lid hint — mesh eyes ~ local (±0.15, 0.25, 0.39)
  vec2 eyeL = vec2(-0.155, 0.252);
  vec2 eyeR = vec2(0.152, 0.251);
  float dL = length((vLocal.xy - eyeL) / vec2(0.055, 0.028));
  float dR = length((vLocal.xy - eyeR) / vec2(0.055, 0.028));
  float eyeMask = 1.0 - smoothstep(0.55, 1.15, min(dL, dR));
  eyeMask *= smoothstep(0.25, 0.45, vLocal.z);
  float iris = 1.0 - smoothstep(0.15, 0.55, min(dL, dR));
  float lid = eyeMask * (0.35 + 0.65 * (1.0 - uBlink));
  base += vec3(0.15, 0.75, 0.95) * lid * 0.55;
  base += vec3(0.4, 0.95, 1.0) * iris * (1.0 - uBlink) * 0.85;
  // Blink darkens the slit
  base *= mix(1.0, 0.22, eyeMask * uBlink);

  float neck = smoothstep(-0.95, -0.58, vWorld.y);
  base *= neck;
  gl_FragColor = vec4(base, 1.0);
}
`

function makeRingMat(color: number, opacity: number) {
  return new THREE.MeshStandardMaterial({
    color,
    emissive: color,
    emissiveIntensity: 0.85,
    metalness: 0.35,
    roughness: 0.35,
    transparent: true,
    opacity,
    depthTest: true,
    depthWrite: false,
    side: THREE.DoubleSide,
  })
}

function makeRing(radius: number, tube: number, color: number, opacity: number, arc = Math.PI * 2) {
  const m = new THREE.Mesh(
    new THREE.TorusGeometry(radius, tube, 14, 160, arc),
    makeRingMat(color, opacity),
  )
  m.renderOrder = 0
  m.frustumCulled = false
  m.castShadow = false
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

    const camera = new THREE.PerspectiveCamera(30, 1, 0.05, 40)
    camera.position.set(0, -0.04, 2.55)
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

    // Soft key + rim for torus emissive response
    const key = new THREE.DirectionalLight(0xffc08a, 0.55)
    key.position.set(1.2, 0.8, 1.5)
    const fill = new THREE.DirectionalLight(0x5ad6ff, 0.35)
    fill.position.set(-1.0, -0.2, 0.8)
    const amb = new THREE.AmbientLight(0x1a2230, 0.45)
    scene.add(key, fill, amb)

    const material = new THREE.ShaderMaterial({
      vertexShader: VERT,
      fragmentShader: FRAG,
      uniforms: {
        uTime: { value: 0 },
        uGlow: { value: 1 },
        uBlink: { value: 0 },
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

    // Deep HUD group — rings behind face plane, strong tilts for 3D read
    const hud = new THREE.Group()
    hud.position.set(0, -0.02, -0.55)
    scene.add(hud)

    const amber = 0xff9a3c
    const cyan = 0x5ad6ff

    // Tilts ~15–70° (0.26–1.22 rad), thicker tubes
    const ringA = makeRing(0.88, 0.028, amber, 0.9)
    ringA.rotation.set(0.55, 0.08, 0)
    const ringB = makeRing(1.02, 0.022, cyan, 0.72)
    ringB.rotation.set(-0.72, 0.2, 0.1)
    ringB.position.z = -0.12
    const ringC = makeRing(1.18, 0.018, amber, 0.55)
    ringC.rotation.set(1.05, -0.12, 0.25)
    ringC.position.z = -0.28
    const ringD = makeRing(1.32, 0.014, cyan, 0.4)
    ringD.rotation.set(-0.95, 0.35, -0.2)
    ringD.position.z = -0.42

    const arc1 = makeRing(0.94, 0.032, amber, 0.92, Math.PI * 0.62)
    arc1.rotation.set(0.48, 0.1, 0.85)
    const arc2 = makeRing(1.1, 0.026, cyan, 0.75, Math.PI * 0.5)
    arc2.rotation.set(-0.65, -0.15, -1.15)
    arc2.position.z = -0.1
    const arc3 = makeRing(1.26, 0.02, amber, 0.55, Math.PI * 0.42)
    arc3.rotation.set(0.9, 0.08, 2.2)
    arc3.position.z = -0.22

    const ticks = new THREE.Group()
    ticks.rotation.x = 0.55
    for (let i = 0; i < 48; i++) {
      const a = (i / 48) * Math.PI * 2
      const len = i % 5 === 0 ? 0.05 : 0.024
      const tick = new THREE.Mesh(
        new THREE.BoxGeometry(0.007, len, 0.007),
        new THREE.MeshStandardMaterial({
          color: amber,
          emissive: amber,
          emissiveIntensity: i % 5 === 0 ? 0.9 : 0.4,
          transparent: true,
          opacity: i % 5 === 0 ? 0.9 : 0.45,
          depthTest: true,
          depthWrite: false,
        }),
      )
      tick.position.set(Math.cos(a) * 0.88, Math.sin(a) * 0.88, 0)
      tick.rotation.z = a
      ticks.add(tick)
    }

    const waveGroup = new THREE.Group()
    waveGroup.rotation.x = 0.5
    waveGroup.position.z = 0.04
    const waveBars: THREE.Mesh[] = []
    for (let i = 0; i < 56; i++) {
      const a = (i / 56) * Math.PI * 2
      const bar = new THREE.Mesh(
        new THREE.BoxGeometry(0.012, 0.06, 0.012),
        new THREE.MeshStandardMaterial({
          color: i % 2 === 0 ? amber : cyan,
          emissive: i % 2 === 0 ? amber : cyan,
          emissiveIntensity: 0.7,
          transparent: true,
          opacity: 0.6,
          depthTest: true,
          depthWrite: false,
        }),
      )
      bar.position.set(Math.cos(a) * 0.78, Math.sin(a) * 0.78, 0)
      bar.rotation.z = a
      waveGroup.add(bar)
      waveBars.push(bar)
    }

    // Data particles around / behind head
    const particleN = 120
    const pPos = new Float32Array(particleN * 3)
    const pCol = new Float32Array(particleN * 3)
    for (let i = 0; i < particleN; i++) {
      const a = Math.random() * Math.PI * 2
      const elev = (Math.random() - 0.5) * 1.1
      const r = 0.95 + Math.random() * 0.55
      pPos[i * 3] = Math.cos(a) * r
      pPos[i * 3 + 1] = elev
      pPos[i * 3 + 2] = -0.15 - Math.random() * 0.7
      const amberish = Math.random() > 0.45
      pCol[i * 3] = amberish ? 1.0 : 0.35
      pCol[i * 3 + 1] = amberish ? 0.6 : 0.85
      pCol[i * 3 + 2] = amberish ? 0.25 : 1.0
    }
    const pGeo = new THREE.BufferGeometry()
    pGeo.setAttribute('position', new THREE.BufferAttribute(pPos, 3))
    pGeo.setAttribute('color', new THREE.BufferAttribute(pCol, 3))
    const particles = new THREE.Points(
      pGeo,
      new THREE.PointsMaterial({
        size: 0.018,
        vertexColors: true,
        transparent: true,
        opacity: 0.75,
        depthTest: true,
        depthWrite: false,
        sizeAttenuation: true,
      }),
    )
    particles.position.z = -0.2

    hud.add(ringA, ringB, ringC, ringD, arc1, arc2, arc3, ticks, waveGroup, particles)

    let mesh: THREE.Mesh | null = null
    let holo: HoloMesh | null = null
    let posAttr: THREE.BufferAttribute | null = null
    const scratch = { arr: null as Float32Array | null }
    const baseScale = 0.68

    let yaw = 0
    let pitch = 0
    let targetYaw = 0
    let targetPitch = 0
    let dragging = false
    let lastX = 0
    let lastY = 0
    let ptrX = 0
    let ptrY = 0
    let nextBlinkAt = 2.5
    let blinkT = 0

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
        geo.setAttribute('normal', new THREE.BufferAttribute(holo.normals.slice(), 3))
        geo.setAttribute('color', new THREE.BufferAttribute(holo.colors, 4))
        geo.setIndex(new THREE.BufferAttribute(holo.indices, 1))
        posAttr = geo.getAttribute('position') as THREE.BufferAttribute

        mesh = new THREE.Mesh(geo, material)
        mesh.renderOrder = 3
        mesh.scale.setScalar(baseScale)
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

      // Blink cycle
      if (t > nextBlinkAt) {
        blinkT = 0.14
        nextBlinkAt = t + 2.8 + Math.random() * 3.5
      }
      blinkT = Math.max(0, blinkT - 1 / 60)
      const blinkAmt = blinkT > 0.07 ? (0.14 - blinkT) / 0.07 : blinkT / 0.07

      material.uniforms.uTime.value = t
      material.uniforms.uGlow.value = speakingRef.current ? 1.12 : 1.0
      material.uniforms.uBlink.value = Math.min(1, Math.max(0, blinkAmt))
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

      // Breathing
      if (mesh) {
        const breath = 1 + Math.sin(t * 1.15) * 0.012
        mesh.scale.setScalar(baseScale * breath)
      }

      // Camera: mouse parallax + slow orbit drift
      const orbit = Math.sin(t * 0.11) * 0.08
      const orbitY = Math.cos(t * 0.09) * 0.03
      const camX = ptrX * 0.12 + orbit
      const camY = -0.04 + ptrY * 0.06 + orbitY
      camera.position.x += (camX - camera.position.x) * 0.06
      camera.position.y += (camY - camera.position.y) * 0.06
      camera.position.z = 2.55
      camera.lookAt(0, -0.08, 0)

      // Multi-axis ring spin
      ringA.rotation.z = t * 0.22
      ringA.rotation.y = Math.sin(t * 0.15) * 0.08
      ringB.rotation.z = -t * 0.16
      ringB.rotation.x = -0.72 + Math.sin(t * 0.2) * 0.06
      ringC.rotation.z = t * 0.11
      ringC.rotation.y = t * 0.05
      ringD.rotation.z = -t * 0.08
      arc1.rotation.z = t * 0.3 + 0.85
      arc2.rotation.z = -t * 0.24 - 1.15
      arc3.rotation.z = t * 0.18 + 2.2
      ticks.rotation.z = t * 0.22
      waveGroup.rotation.z = t * 0.22
      particles.rotation.y = t * 0.07
      particles.rotation.z = Math.sin(t * 0.13) * 0.1
      hud.rotation.y = ptrX * 0.04
      hud.rotation.x = ptrY * 0.025

      if (holo && posAttr && scratch.arr && mesh) {
        const m = mouthRef.current
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
        ;(waveBars[i].material as THREE.MeshStandardMaterial).opacity = speak ? 0.8 : 0.45
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
