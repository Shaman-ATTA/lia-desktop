import { useEffect, useRef } from 'react'
import * as THREE from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'

export type MouthWeights = { jaw: number; funnel: number; wide: number }

type Props = {
  mouth: MouthWeights
  speaking?: boolean
  /** Capture / demo: hold a yaw pose (radians). */
  poseYaw?: number
  /** Capture: amplify idle/camera motion. */
  motionBoost?: number
}

const MORPH = {
  jawOpen: 'jawOpen',
  mouthClose: 'mouthClose',
  mouthFunnel: 'mouthFunnel',
  mouthPucker: 'mouthPucker',
  mouthSmile: 'mouthSmile',
  blinkL: 'eyeBlink_L',
  blinkR: 'eyeBlink_R',
  browUp: 'browUp',
} as const

function applyHoloMaterial(mesh: THREE.Mesh, kind: 'head' | 'eye' | 'cavity') {
  const prev = mesh.material as THREE.Material
  const hasColor = !!(mesh.geometry.getAttribute('color') || mesh.geometry.getAttribute('color_0'))
  if (kind === 'cavity') {
    mesh.material = new THREE.MeshStandardMaterial({
      color: new THREE.Color(0.015, 0.04, 0.06),
      emissive: new THREE.Color(0.02, 0.08, 0.1),
      emissiveIntensity: 0.35,
      roughness: 0.9,
      metalness: 0.05,
      transparent: true,
      opacity: 0.92,
      side: THREE.DoubleSide,
      depthWrite: true,
    })
  } else if (kind === 'eye') {
    mesh.material = new THREE.MeshStandardMaterial({
      color: new THREE.Color(0.05, 0.7, 0.85),
      emissive: new THREE.Color(0.1, 0.55, 0.7),
      emissiveIntensity: 0.8,
      roughness: 0.25,
      metalness: 0.4,
      transparent: true,
      opacity: 0.95,
    })
  } else {
    const mat = new THREE.MeshStandardMaterial({
      color: new THREE.Color(0.08, 0.35, 0.45),
      emissive: new THREE.Color(0.05, 0.45, 0.55),
      emissiveIntensity: 0.55,
      roughness: 0.32,
      metalness: 0.55,
      transparent: true,
      opacity: 0.88,
      side: THREE.DoubleSide,
      vertexColors: hasColor,
      depthWrite: false,
    })
    mat.onBeforeCompile = (shader) => {
      shader.uniforms.uTime = { value: 0 }
      ;(mat as THREE.MeshStandardMaterial & { userData: { uTime?: { value: number } } }).userData.uTime =
        shader.uniforms.uTime
      shader.vertexShader = shader.vertexShader
        .replace(
          '#include <common>',
          `#include <common>\nvarying vec3 vHoloWorld;`,
        )
        .replace(
          '#include <begin_vertex>',
          `#include <begin_vertex>\nvHoloWorld = (modelMatrix * vec4(transformed, 1.0)).xyz;`,
        )
      shader.fragmentShader = shader.fragmentShader
        .replace(
          '#include <common>',
          `#include <common>\nuniform float uTime;\nvarying vec3 vHoloWorld;`,
        )
        .replace(
          '#include <output_fragment>',
          `
          float scanWave = 0.5 + 0.5 * sin((vHoloWorld.y * 155.0 + uTime * 1.7) * 3.14159265);
          float scan = pow(scanWave, 10.0) * 0.22;
          float speck = 0.03 * step(0.955, fract(vHoloWorld.x * 70.0 + vHoloWorld.y * 70.0));
          outgoingLight += vec3(0.15, 0.75, 0.95) * (scan + speck);
          outgoingLight += vec3(0.18, 0.8, 1.0) * 0.12;
          // Neck → transparent (no black blob); wide soft falloff
          float neck = smoothstep(-0.75, 0.05, vHoloWorld.y);
          diffuseColor.a *= clamp(pow(neck, 1.35) * 0.98, 0.0, 0.95);
          outgoingLight *= mix(0.35, 1.0, neck);
          #include <output_fragment>
          `,
        )
    }
    mesh.material = mat
  }
  prev?.dispose?.()
}

function makeRing(radius: number, tube: number, color: number, opacity: number, arc = Math.PI * 2) {
  const geo = new THREE.TorusGeometry(radius, tube, 10, 96, arc)
  const mat = new THREE.MeshBasicMaterial({
    color,
    transparent: true,
    opacity,
    depthWrite: false,
    side: THREE.DoubleSide,
  })
  return new THREE.Mesh(geo, mat)
}

function makeTicks(radius: number, count: number, color: number) {
  const g = new THREE.Group()
  for (let i = 0; i < count; i++) {
    const a = (i / count) * Math.PI * 2
    const len = i % 5 === 0 ? 0.055 : 0.028
    const geo = new THREE.BoxGeometry(0.006, len, 0.006)
    const mat = new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: i % 5 === 0 ? 0.75 : 0.35,
      depthWrite: false,
    })
    const m = new THREE.Mesh(geo, mat)
    m.position.set(Math.cos(a) * radius, Math.sin(a) * radius, 0)
    m.rotation.z = a
    g.add(m)
  }
  return g
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
    scene.fog = null

    const camera = new THREE.PerspectiveCamera(28, 1, 0.05, 40)
    camera.position.set(0, 0.02, 1.95)
    camera.lookAt(0, -0.04, 0)

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
        '<div style="display:flex;align-items:center;justify-content:center;height:100%;color:#FFB347;letter-spacing:.14em">ЛИА · 3D</div>'
      return
    }
    renderer.setClearColor(0x000000, 0)
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2))
    el.appendChild(renderer.domElement)

    const amb = new THREE.AmbientLight(0x446688, 0.55)
    const key = new THREE.DirectionalLight(0xa8efff, 1.05)
    key.position.set(1.2, 1.4, 2.2)
    const rim = new THREE.DirectionalLight(0xff9a3c, 0.55)
    rim.position.set(-1.4, 0.4, -0.8)
    const fill = new THREE.PointLight(0x66ddff, 0.6, 8)
    fill.position.set(0, 0.2, 1.5)
    scene.add(amb, key, rim, fill)

    const root = new THREE.Group()
    scene.add(root)

    // ——— 3D HUD rings / arcs / ticks (depth + parallax) ———
    const hud = new THREE.Group()
    hud.position.set(0, -0.05, 0)
    scene.add(hud)

    const amber = 0xff9a3c
    const cyan = 0x5ad6ff

    const ringA = makeRing(0.92, 0.008, amber, 0.55)
    ringA.rotation.x = Math.PI / 2.35
    ringA.position.z = -0.15
    const ringB = makeRing(1.08, 0.006, cyan, 0.4)
    ringB.rotation.x = Math.PI / 2.1
    ringB.rotation.y = 0.25
    ringB.position.z = 0.05
    const ringC = makeRing(1.22, 0.005, amber, 0.28)
    ringC.rotation.x = Math.PI / 2.55
    ringC.rotation.z = 0.4
    ringC.position.z = -0.35

    const arc1 = makeRing(1.0, 0.012, amber, 0.7, Math.PI * 0.55)
    arc1.rotation.set(Math.PI / 2.2, 0.1, 0.8)
    arc1.position.z = 0.18
    const arc2 = makeRing(1.15, 0.01, cyan, 0.55, Math.PI * 0.4)
    arc2.rotation.set(Math.PI / 2.4, -0.2, -1.1)
    arc2.position.z = -0.22

    const ticks = makeTicks(1.02, 48, amber)
    ticks.rotation.x = Math.PI / 2.3
    ticks.position.z = 0.02

    hud.add(ringA, ringB, ringC, arc1, arc2, ticks)

    // Radial waveform bars (voice-reactive)
    const waveGroup = new THREE.Group()
    waveGroup.rotation.x = Math.PI / 2.25
    waveGroup.position.z = 0.12
    const waveBars: THREE.Mesh[] = []
    const WAVE_N = 64
    for (let i = 0; i < WAVE_N; i++) {
      const a = (i / WAVE_N) * Math.PI * 2
      const geo = new THREE.BoxGeometry(0.018, 0.08, 0.018)
      const mat = new THREE.MeshBasicMaterial({
        color: i % 2 === 0 ? amber : cyan,
        transparent: true,
        opacity: 0.55,
        depthWrite: false,
      })
      const bar = new THREE.Mesh(geo, mat)
      bar.position.set(Math.cos(a) * 0.78, Math.sin(a) * 0.78, 0)
      bar.rotation.z = a
      waveGroup.add(bar)
      waveBars.push(bar)
    }
    hud.add(waveGroup)

    // Floating data diamonds
    for (let i = 0; i < 6; i++) {
      const a = (i / 6) * Math.PI * 2
      const d = new THREE.Mesh(
        new THREE.OctahedronGeometry(0.025, 0),
        new THREE.MeshBasicMaterial({ color: amber, transparent: true, opacity: 0.65, depthWrite: false }),
      )
      d.position.set(Math.cos(a) * 1.28, 0.15 + Math.sin(a * 2) * 0.08, Math.sin(a) * 0.35)
      hud.add(d)
    }

    let headMesh: THREE.Mesh | null = null
    let cavityMesh: THREE.Object3D | null = null
    const morphMeshes: THREE.Mesh[] = []

    let yaw = 0
    let pitch = 0
    let targetYaw = 0
    let targetPitch = 0
    let dragging = false
    let lastX = 0
    let lastY = 0
    let ptrX = 0
    let ptrY = 0
    let blinkT = 2.5 + Math.random() * 2

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
      // Spring toward poseYaw (capture) or frontal
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

    const loadGlb = async () => {
      const loader = new GLTFLoader()
      const tryUrls: Array<string | ArrayBuffer> = []

      try {
        if (window.lia?.glbBytes) {
          const bytes = await window.lia.glbBytes()
          tryUrls.push(bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength))
        }
      } catch (e) {
        console.warn('glbBytes', e)
      }
      tryUrls.push(
        new URL('models/lia_face_morph.glb', document.baseURI).href,
        './models/lia_face_morph.glb',
        'models/lia_face_morph.glb',
      )

      let gltf: Awaited<ReturnType<typeof loader.loadAsync>> | null = null
      let lastErr: unknown
      for (const u of tryUrls) {
        try {
          if (typeof u !== 'string') {
            gltf = await new Promise((resolve, reject) => {
              loader.parse(u, '', resolve, reject)
            })
          } else {
            gltf = await loader.loadAsync(u)
          }
          lastErr = null
          break
        } catch (e) {
          lastErr = e
        }
      }
      if (!gltf) throw lastErr ?? new Error('GLB load failed')

      const model = gltf.scene
      model.traverse((obj) => {
        if (!(obj instanceof THREE.Mesh)) return
        const name = (obj.name || '').toLowerCase()
        // GLB mouth_cavity is a flat card → hide forever (black slab). Jaw morph opens lips.
        if (name.includes('cavity') || name.includes('mouth')) {
          obj.visible = false
          obj.raycast = () => {}
          return
        }
        if (name.includes('eye')) {
          applyHoloMaterial(obj, 'eye')
        } else {
          applyHoloMaterial(obj, 'head')
          if (obj.morphTargetDictionary && obj.morphTargetInfluences) {
            headMesh = obj
            morphMeshes.push(obj)
          }
        }
      })

      // Soft recessed oral bowl parented to head (not a flat slab)
      const cavityGeo = new THREE.SphereGeometry(0.045, 20, 16, 0, Math.PI * 2, 0, Math.PI * 0.55)
      const cavityMat = new THREE.MeshStandardMaterial({
        color: new THREE.Color(0.02, 0.06, 0.09),
        emissive: new THREE.Color(0.04, 0.18, 0.22),
        emissiveIntensity: 0.45,
        roughness: 0.85,
        metalness: 0.05,
        transparent: true,
        opacity: 0.9,
        side: THREE.BackSide,
        depthWrite: false,
      })
      const cavity = new THREE.Mesh(cavityGeo, cavityMat)
      cavity.name = 'soft_cavity'
      cavity.visible = false
      cavity.scale.set(1.1, 0.55, 0.85)
      // Approx mouth from landmarks (pre-scale local space)
      cavity.position.set(0, 0.07, 0.42)
      model.add(cavity)
      cavityMesh = cavity

      // Fit head to ~70% of vertical FOV
      const box = new THREE.Box3().setFromObject(model)
      const size = box.getSize(new THREE.Vector3())
      const center = box.getCenter(new THREE.Vector3())
      const targetH = 1.72
      const s = targetH / Math.max(size.y, 0.001)
      model.scale.setScalar(s)
      model.position.set(-center.x * s, -center.y * s + 0.02, -center.z * s)
      root.add(model)
    }

    void loadGlb().catch((err) => console.error('holo glb', err))

    let raf = 0
    const t0 = performance.now()

    const setMorph = (mesh: THREE.Mesh, name: string, v: number) => {
      const dict = mesh.morphTargetDictionary
      const infl = mesh.morphTargetInfluences
      if (!dict || !infl) return
      const idx = dict[name]
      if (idx === undefined) return
      infl[idx] = v
    }

    const tick = () => {
      raf = requestAnimationFrame(tick)
      const t = (performance.now() - t0) / 1000
      const boost = boostRef.current

      // Spring head — idle sway (suppressed when a held poseYaw is set for capture)
      if (!dragging) {
        const pose = poseYawRef.current
        const hold = Math.abs(pose) > 0.15
        const idleYaw = hold
          ? 0
          : Math.sin(t * 0.35) * 0.08 * boost + Math.sin(t * 0.17) * 0.04 * boost
        const idlePitch = hold
          ? Math.sin(t * 0.5) * 0.01
          : Math.sin(t * 0.28) * 0.03 * boost + Math.sin(t * 0.9) * 0.012 * boost
        const breath = Math.sin(t * 1.15) * 0.01 * boost
        targetYaw = pose + idleYaw
        targetPitch = idlePitch + (hold ? 0 : breath)
      }
      const holdPose = Math.abs(poseYawRef.current) > 0.15
      const follow = dragging ? 0.22 : holdPose ? 0.4 : 0.08
      yaw += (targetYaw - yaw) * follow
      pitch += (targetPitch - pitch) * follow
      if (holdPose && Math.abs(targetYaw - yaw) < 0.02) yaw = targetYaw
      root.rotation.y = yaw
      root.rotation.x = pitch
      root.position.y = holdPose ? 0 : Math.sin(t * 1.15) * 0.012 * boost

      // Camera drift + mouse parallax (orbit a bit when posing so depth reads clearly)
      const camBaseZ = 1.95
      const driftX =
        (holdPose ? Math.sin(poseYawRef.current) * 0.35 : Math.sin(t * 0.22) * 0.12 * boost) +
        ptrX * 0.18
      const driftY = Math.cos(t * 0.18) * 0.06 * boost + ptrY * 0.1
      camera.position.x += (driftX - camera.position.x) * 0.06
      camera.position.y += (0.02 + driftY - camera.position.y) * 0.06
      camera.position.z = camBaseZ + Math.sin(t * 0.15) * 0.08 * boost
      camera.lookAt(0, -0.04 + Math.sin(t * 0.2) * 0.02, 0)

      // HUD rings spin at different depths
      ringA.rotation.z = t * 0.25
      ringB.rotation.z = -t * 0.18
      ringC.rotation.z = t * 0.12
      arc1.rotation.z = t * 0.4 + 0.8
      arc2.rotation.z = -t * 0.32 - 1.1
      ticks.rotation.z = t * 0.08
      waveGroup.rotation.z = t * 0.15
      hud.position.x = ptrX * 0.06
      hud.position.y = -0.05 + ptrY * 0.04
      hud.rotation.y = ptrX * 0.08
      hud.rotation.x = -ptrY * 0.05

      const m = mouthRef.current
      const speak = speakingRef.current
      const jaw = Math.min(1, Math.max(0, m.jaw * 1.2))
      const funnel = Math.min(1, m.funnel)
      const smile = Math.min(1, m.wide * 0.85)

      // Blink
      blinkT -= 1 / 60
      let blink = 0
      if (blinkT < 0.12) blink = 1 - Math.abs(blinkT - 0.06) / 0.06
      if (blinkT < 0) blinkT = 2.2 + Math.random() * 3.5

      for (const mesh of morphMeshes) {
        setMorph(mesh, MORPH.jawOpen, jaw)
        setMorph(mesh, MORPH.mouthClose, 0)
        setMorph(mesh, MORPH.mouthFunnel, funnel)
        setMorph(mesh, MORPH.mouthPucker, funnel * 0.35)
        setMorph(mesh, MORPH.mouthSmile, smile)
        setMorph(mesh, MORPH.blinkL, blink)
        setMorph(mesh, MORPH.blinkR, blink)
        setMorph(mesh, MORPH.browUp, speak ? 0.15 + 0.1 * Math.sin(t * 3) : 0.05)
        const mat = mesh.material as THREE.MeshStandardMaterial & {
          userData: { uTime?: { value: number } }
        }
        if (mat.userData?.uTime) mat.userData.uTime.value = t
        mat.emissiveIntensity = speak ? 0.75 : 0.55
      }
      if (cavityMesh) {
        const open = jaw > 0.25
        cavityMesh.visible = open
        if (open) {
          cavityMesh.scale.set(1.05 + jaw * 0.25, 0.4 + jaw * 0.55, 0.75 + jaw * 0.2)
        }
      }

      // Waveform reacts to jaw / speaking
      for (let i = 0; i < waveBars.length; i++) {
        const phase = t * (speak ? 9 : 2.2) + i * 0.4
        const base = speak ? 0.35 + jaw * 0.9 : 0.12
        const h = base * (0.45 + 0.55 * Math.abs(Math.sin(phase))) * (0.7 + 0.3 * Math.sin(i + t))
        waveBars[i].scale.y = Math.max(0.15, h * 4.5)
        const mat = waveBars[i].material as THREE.MeshBasicMaterial
        mat.opacity = speak ? 0.75 : 0.35
      }

      renderer.render(scene, camera)
    }
    tick()

    // Expose capture helpers on element dataset via custom events
    const onPose = (ev: Event) => {
      const d = (ev as CustomEvent).detail || {}
      if (typeof d.yaw === 'number') {
        poseYawRef.current = d.yaw
        targetYaw = d.yaw
      }
      if (typeof d.boost === 'number') boostRef.current = d.boost
    }
    el.addEventListener('lia-pose', onPose as EventListener)

    return () => {
      cancelAnimationFrame(raf)
      ro.disconnect()
      el.removeEventListener('pointerdown', onDown)
      el.removeEventListener('pointermove', onMove)
      el.removeEventListener('pointerup', onUp)
      el.removeEventListener('pointercancel', onUp)
      el.removeEventListener('lia-pose', onPose as EventListener)
      renderer.dispose()
      if (renderer.domElement.parentElement === el) el.removeChild(renderer.domElement)
    }
  }, [])

  // Keep pose/boost in sync without remounting
  useEffect(() => {
    poseYawRef.current = poseYaw
  }, [poseYaw])
  useEffect(() => {
    boostRef.current = motionBoost
  }, [motionBoost])

  return <div className="holo-stage" ref={mountRef} data-testid="holo-stage" />
}
