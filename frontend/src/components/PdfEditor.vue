<script setup>
import { ref, computed, reactive, watch } from 'vue'
import {
  createSession,
  insertFile,
  updatePage,
  deletePage,
  deletePageRange,
  reorderPages,
  renderPageUrl,
  exportSessionWithProgress,
  trackDownload,
} from '../api/pdfApi.js'

const DESKTOP_DOWNLOAD_URL = 'https://github.com/rmsals8/WpfApp1/releases/download/v1.0.0/AIOpdfSetup.exe'

function trackDesktopDownload() {
  // 다운로드 자체는 href로 즐시 진행되고, 카운트는 실패해도 다운로드를 막지 않도록 fire-and-forget로 처리
  trackDownload('desktop-windows').catch(() => {})
}

const sessionId = ref(null)
const pages = ref([])
const maxPages = ref(30)
const selectedPageId = ref(null)
const renderTick = reactive({}) // pageId -> counter, thumbnail cache-busting after edits

const status = reactive({
  uploading: false,
  inserting: false,
  exporting: false,
  error: '',
  trialBlocked: false,
})

const exportProgress = reactive({ completed: 0, total: 0 })

const rangeFrom = ref(1)
const rangeTo = ref(1)
const rangeError = ref('')

const fileInput = ref(null)
const insertInput = ref(null)
const dragPageId = ref(null)
const cropBox = ref(null) // { x, y, w, h } in 0..1, while dragging
const isDraggingCrop = ref(false)
const previewFrame = ref(null)

// ---- 서명/그리기(잉크) 편집 상태 ----
const editMode = ref('crop') // 'crop' | 'draw' | 'erase'
const inkColor = ref('#000000')
const inkThicknessRatio = ref(0.006) // 페이지 너비 대비 비율 (WpfApp1의 InkThickness에 대응)
const isDrawing = ref(false)
const drawingPoints = ref([]) // 현재 그리는 중인 획의 점 목록(0~1 정규화), 커밋 전까지만 클라이언트에서 보유

const selectedPage = computed(() => pages.value.find((p) => p.id === selectedPageId.value) || null)
const pageCountLabel = computed(() => `${pages.value.length} / ${maxPages.value === Number.MAX_SAFE_INTEGER || maxPages.value > 9000 ? '무제한' : maxPages.value}`)
const exportPercent = computed(() => {
  if (!exportProgress.total) return 0
  return Math.round((exportProgress.completed / exportProgress.total) * 100)
})

// 페이지가 삽입/삭제될 때마다 "끝"의 기본값을 마지막 페이지 번호로 자동 갱신
watch(
  () => pages.value.length,
  (len) => {
    rangeTo.value = len
    if (rangeFrom.value > len) rangeFrom.value = len || 1
  },
)

function bump(pageId) {
  renderTick[pageId] = (renderTick[pageId] || 0) + 1
}

function thumbUrl(pageId) {
  return renderPageUrl(sessionId.value, pageId, 220, true, renderTick[pageId] || 0)
}

function previewUrl(pageId) {
  return renderPageUrl(sessionId.value, pageId, 1000, false, renderTick[pageId] || 0)
}

async function onUploadChange(e) {
  const file = e.target.files?.[0]
  if (!file) return
  status.error = ''
  status.uploading = true
  try {
    const session = await createSession(file)
    sessionId.value = session.sessionId
    pages.value = session.pages
    maxPages.value = session.maxPages
    selectedPageId.value = session.pages[0]?.id ?? null
  } catch (err) {
    status.error = err.message || '업로드 중 오류가 발생했습니다.'
  } finally {
    status.uploading = false
    e.target.value = ''
  }
}

async function onInsertChange(e) {
  const file = e.target.files?.[0]
  if (!file || !sessionId.value) return
  status.error = ''
  status.inserting = true
  try {
    const afterIndex = selectedPage.value ? selectedPage.value.displayIndex : pages.value.length
    const session = await insertFile(sessionId.value, file, afterIndex)
    pages.value = session.pages
    maxPages.value = session.maxPages
  } catch (err) {
    status.error = err.message || '삽입 중 오류가 발생했습니다.'
  } finally {
    status.inserting = false
    e.target.value = ''
  }
}

function selectPage(id) {
  selectedPageId.value = id
  cropBox.value = null
  editMode.value = 'crop'
  drawingPoints.value = []
}

async function patchSelected(patch) {
  if (!selectedPage.value) return
  const id = selectedPage.value.id
  try {
    const updated = await updatePage(sessionId.value, id, patch)
    const idx = pages.value.findIndex((p) => p.id === id)
    if (idx !== -1) pages.value[idx] = { ...pages.value[idx], ...updated }
    bump(id)
  } catch (err) {
    status.error = err.message || '페이지를 업데이트하지 못했습니다.'
  }
}

function rotate(delta) {
  patchSelected({ rotateBy: delta })
}

let adjustTimer = null
function onAdjustInput(field, value) {
  if (!selectedPage.value) return
  const idx = pages.value.findIndex((p) => p.id === selectedPage.value.id)
  if (idx !== -1) pages.value[idx][field] = Number(value)
  clearTimeout(adjustTimer)
  adjustTimer = setTimeout(() => patchSelected({ [field]: Number(value) }), 180)
}

function toggleAutoExposure(e) {
  patchSelected({ autoExposure: e.target.checked })
}

function resetCrop() {
  patchSelected({ cropX: 0, cropY: 0, cropWidth: 1, cropHeight: 1 })
}

async function removePage(id) {
  if (!sessionId.value) return
  try {
    const session = await deletePage(sessionId.value, id)
    pages.value = session.pages
    if (selectedPageId.value === id) {
      selectedPageId.value = pages.value[0]?.id ?? null
    }
  } catch (err) {
    status.error = err.message || '페이지를 삭제하지 못했습니다.'
  }
}

// ---- drag reorder ----
function onDragStart(id) {
  dragPageId.value = id
}

async function onDrop(targetId) {
  if (!dragPageId.value || dragPageId.value === targetId) return
  const list = [...pages.value]
  const fromIdx = list.findIndex((p) => p.id === dragPageId.value)
  const toIdx = list.findIndex((p) => p.id === targetId)
  const [moved] = list.splice(fromIdx, 1)
  list.splice(toIdx, 0, moved)
  pages.value = list
  dragPageId.value = null
  try {
    const session = await reorderPages(sessionId.value, list.map((p) => p.id))
    pages.value = session.pages
  } catch (err) {
    status.error = err.message || '순서를 변경하지 못했습니다.'
  }
}

// ---- crop drag-select on the large preview ----
function getNormalizedPoint(e) {
  const rect = previewFrame.value.getBoundingClientRect()
  const x = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
  const y = Math.min(1, Math.max(0, (e.clientY - rect.top) / rect.height))
  return { x, y }
}

function onCropMouseDown(e) {
  if (!previewFrame.value) return
  isDraggingCrop.value = true
  const rect = previewFrame.value.getBoundingClientRect()
  const x = (e.clientX - rect.left) / rect.width
  const y = (e.clientY - rect.top) / rect.height
  cropBox.value = { x, y, w: 0, h: 0 }
}

function onCropMouseMove(e) {
  if (!isDraggingCrop.value || !previewFrame.value || !cropBox.value) return
  const rect = previewFrame.value.getBoundingClientRect()
  const x = Math.min(1, Math.max(0, (e.clientX - rect.left) / rect.width))
  const y = Math.min(1, Math.max(0, (e.clientY - rect.top) / rect.height))
  cropBox.value = {
    x: Math.min(cropBox.value.x, x),
    y: Math.min(cropBox.value.y, y),
    w: Math.abs(x - cropBox.value.x),
    h: Math.abs(y - cropBox.value.y),
  }
}

function onCropMouseUp() {
  if (!isDraggingCrop.value) return
  isDraggingCrop.value = false
  if (!cropBox.value || cropBox.value.w < 0.02 || cropBox.value.h < 0.02) {
    cropBox.value = null
    return
  }
  const cx = cropBox.value.x
  const cy = cropBox.value.y
  patchSelected({ cropX: cx, cropY: cy, cropWidth: cropBox.value.w, cropHeight: cropBox.value.h })
}

// ---- 서명/그리기 및 지우개: 모드에 따라 자르기/그리기/지우기로 분기 ----
function toInkStrokeDto(stroke) {
  return {
    points: stroke.points.map((p) => ({ x: p.x, y: p.y })),
    color: stroke.color,
    thicknessRatio: stroke.thicknessRatio,
  }
}

function onPreviewMouseDown(e) {
  if (!previewFrame.value) return
  if (editMode.value === 'crop') {
    onCropMouseDown(e)
    return
  }
  if (editMode.value === 'draw') {
    isDrawing.value = true
    drawingPoints.value = [getNormalizedPoint(e)]
    return
  }
  if (editMode.value === 'erase') {
    isDrawing.value = true
    eraseAt(getNormalizedPoint(e))
  }
}

function onPreviewMouseMove(e) {
  if (editMode.value === 'crop') {
    onCropMouseMove(e)
    return
  }
  if (editMode.value === 'draw' && isDrawing.value) {
    drawingPoints.value.push(getNormalizedPoint(e))
    return
  }
  if (editMode.value === 'erase' && isDrawing.value) {
    eraseAt(getNormalizedPoint(e))
  }
}

async function onPreviewMouseUp() {
  if (editMode.value === 'crop') {
    onCropMouseUp()
    return
  }
  if (editMode.value === 'draw') {
    isDrawing.value = false
    if (drawingPoints.value.length >= 2 && selectedPage.value) {
      const newStroke = {
        points: drawingPoints.value.map((p) => ({ x: p.x, y: p.y })),
        color: inkColor.value,
        thicknessRatio: inkThicknessRatio.value,
      }
      const existing = (selectedPage.value.inkStrokes || []).map(toInkStrokeDto)
      drawingPoints.value = []
      await patchSelected({ inkStrokes: [...existing, newStroke] })
      return
    }
    drawingPoints.value = []
    return
  }
  if (editMode.value === 'erase') {
    isDrawing.value = false
  }
}

function onPreviewMouseLeave() {
  isDraggingCrop.value = false
  isDrawing.value = false
  drawingPoints.value = []
}

let eraseInFlight = false
async function eraseAt(p) {
  if (!selectedPage.value || eraseInFlight) return
  const strokes = selectedPage.value.inkStrokes || []
  const threshold = 0.025
  const remaining = strokes.filter(
    (s) => !s.points.some((pt) => Math.hypot(pt.x - p.x, pt.y - p.y) < threshold),
  )
  if (remaining.length === strokes.length) return
  eraseInFlight = true
  try {
    await patchSelected({ inkStrokes: remaining.map(toInkStrokeDto) })
  } finally {
    eraseInFlight = false
  }
}

function clearInk() {
  patchSelected({ inkStrokes: [] })
}

function setMode(mode) {
  editMode.value = mode
  isDraggingCrop.value = false
  isDrawing.value = false
  drawingPoints.value = []
  if (mode !== 'crop') cropBox.value = null
}

function setInkColor(color) {
  inkColor.value = color
}

// ---- export ----
async function doExport() {
  if (!sessionId.value) return
  status.error = ''
  status.trialBlocked = false
  status.exporting = true
  exportProgress.completed = 0
  exportProgress.total = pages.value.length
  try {
    const blob = await exportSessionWithProgress(sessionId.value, (completed, total) => {
      exportProgress.completed = completed
      exportProgress.total = total
    })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'export.pdf'
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(url)
  } catch (err) {
    if (err.status === 402) {
      status.trialBlocked = true
      status.error = err.message
    } else {
      status.error = err.message || '내보내기 중 오류가 발생했습니다.'
    }
  } finally {
    status.exporting = false
  }
}

function startOver() {
  sessionId.value = null
  pages.value = []
  selectedPageId.value = null
  status.error = ''
  status.trialBlocked = false
  editMode.value = 'crop'
  drawingPoints.value = []
}

// ---- 범위 삭제 / 분리(스플릿) ----
function validateRange() {
  const from = Number(rangeFrom.value)
  const to = Number(rangeTo.value)
  if (!from || !to || from < 1 || to < from || to > pages.value.length) {
    rangeError.value = '페이지 범위를 다시 확인해주세요.'
    return null
  }
  rangeError.value = ''
  return { from, to }
}

async function deleteRange() {
  if (!sessionId.value) return
  const range = validateRange()
  if (!range) return
  if (range.to - range.from + 1 >= pages.value.length) {
    rangeError.value = '모든 페이지를 삭제할 수는 없습니다.'
    return
  }
  try {
    const session = await deletePageRange(sessionId.value, range.from, range.to)
    pages.value = session.pages
    if (!pages.value.find((p) => p.id === selectedPageId.value)) {
      selectedPageId.value = pages.value[0]?.id ?? null
    }
    rangeFrom.value = 1
    rangeTo.value = 1
  } catch (err) {
    rangeError.value = err.message || '범위 삭제 중 오류가 발생했습니다.'
  }
}

async function splitRange() {
  if (!sessionId.value) return
  const range = validateRange()
  if (!range) return
  status.error = ''
  status.trialBlocked = false
  status.exporting = true
  exportProgress.completed = 0
  exportProgress.total = range.to - range.from + 1
  try {
    const blob = await exportSessionWithProgress(
      sessionId.value,
      (completed, total) => {
        exportProgress.completed = completed
        exportProgress.total = total
      },
      range,
    )
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = `export_${range.from}-${range.to}.pdf`
    document.body.appendChild(a)
    a.click()
    a.remove()
    URL.revokeObjectURL(url)
  } catch (err) {
    if (err.status === 402) {
      status.trialBlocked = true
      status.error = err.message
    } else {
      status.error = err.message || '분리 내보내기 중 오류가 발생했습니다.'
    }
  } finally {
    status.exporting = false
  }
}
</script>

<template>
  <div class="workspace">
    <!-- 내보내기 중 전체 화면 차단 오버레이: 클릭/키보드 입력을 몰면서 진행률을 보여준다 -->
    <div v-if="status.exporting" class="export-overlay" role="alert" aria-busy="true">
      <div class="export-modal">
        <p class="export-title">내보내는 중입니다…</p>
        <div class="export-bar-track">
          <div class="export-bar-fill" :style="{ width: exportPercent + '%' }" />
        </div>
        <p class="export-count">
          {{ exportProgress.completed }} / {{ exportProgress.total }} 페이지 ({{ exportPercent }}%)
        </p>
        <p class="export-hint">완료될 때까지 이 창을 닫거나 다른 작업을 하지 마세요.</p>
      </div>
    </div>

    <header class="toolbar">
      <div class="brand">
        <span class="brand-mark">AP</span>
        <div class="brand-text">
          <span class="brand-title">ALO PDF</span>
          <span class="brand-sub">웹 PDF 편집기</span>
        </div>
      </div>

      <div class="toolbar-status">
        <a
          class="ghost-btn download-btn"
          :href="DESKTOP_DOWNLOAD_URL"
          @click="trackDesktopDownload"
        >
          ⬇ 데스크톱 앱 다운로드
        </a>
        <template v-if="sessionId">
          <span class="page-count">{{ pageCountLabel }} 페이지</span>
          <button class="ghost-btn" @click="startOver">새 파일</button>
          <button class="primary-btn" :disabled="status.exporting || pages.length === 0" @click="doExport">
            내보내기
          </button>
        </template>
      </div>
    </header>

    <p v-if="status.error" class="banner" :class="{ 'banner-stamp': status.trialBlocked }">
      {{ status.error }}
    </p>

    <!-- empty state -->
    <section v-if="!sessionId" class="dropzone">
      <div class="dropzone-inner">
        <span class="dropzone-mark">＋</span>
        <h1>PDF를 올려서 시작하세요</h1>
        <p>페이지 회전·자르기·밝기 보정을 하고, 최대 {{ maxPages }}페이지까지 편집할 수 있어요.</p>
        <label class="primary-btn upload-btn">
          {{ status.uploading ? '업로드 중…' : 'PDF 선택' }}
          <input ref="fileInput" type="file" accept="application/pdf" hidden @change="onUploadChange" />
        </label>
      </div>
    </section>

    <!-- editor -->
    <section v-else class="editor">
      <aside class="page-tray">
        <div
          v-for="p in pages"
          :key="p.id"
          class="page-card"
          :class="{ active: p.id === selectedPageId }"
          draggable="true"
          @dragstart="onDragStart(p.id)"
          @dragover.prevent
          @drop="onDrop(p.id)"
          @click="selectPage(p.id)"
        >
          <img :src="thumbUrl(p.id)" :alt="`페이지 ${p.displayIndex}`" loading="lazy" />
          <div class="page-card-footer">
            <span class="page-index">{{ String(p.displayIndex).padStart(2, '0') }}</span>
            <button class="icon-btn danger" title="페이지 삭제" @click.stop="removePage(p.id)">✕</button>
          </div>
        </div>

        <label class="page-card add-card">
          <span>{{ status.inserting ? '삽입 중…' : '+ 페이지 삽입' }}</span>
          <input ref="insertInput" type="file" accept="application/pdf,image/png,image/jpeg,image/bmp,image/tiff" hidden @change="onInsertChange" />
        </label>
      </aside>

      <main class="detail-panel">
        <template v-if="selectedPage">
          <div class="ink-toolbar">
            <div class="mode-group">
              <button class="mode-btn" :class="{ active: editMode === 'crop' }" @click="setMode('crop')">✂ 자르기
              </button>
              <button class="mode-btn" :class="{ active: editMode === 'draw' }" @click="setMode('draw')">✒ 서명/그리기
              </button>
              <button class="mode-btn" :class="{ active: editMode === 'erase' }" @click="setMode('erase')">🧹 지우개
              </button>
            </div>
            <div v-if="editMode === 'draw'" class="ink-options">
              <button
                v-for="c in ['#000000', '#0078D4', '#C42B1C']"
                :key="c"
                class="color-swatch"
                :class="{ active: inkColor === c }"
                :style="{ background: c }"
                :title="c"
                @click="setInkColor(c)"
              />
              <span class="control-label">굵기</span>
              <input type="range" min="0.002" max="0.02" step="0.001" v-model.number="inkThicknessRatio" />
            </div>
            <button v-if="editMode !== 'crop'" class="ghost-btn" @click="clearInk">서명 지우기</button>
          </div>

          <div
            ref="previewFrame"
            class="preview-frame"
            :class="`mode-${editMode}`"
            @mousedown="onPreviewMouseDown"
            @mousemove="onPreviewMouseMove"
            @mouseup="onPreviewMouseUp"
            @mouseleave="onPreviewMouseLeave"
          >
            <img :src="previewUrl(selectedPage.id)" alt="선택한 페이지 미리보기" draggable="false" />
            <div
              v-if="cropBox && editMode === 'crop'"
              class="crop-box"
              :style="{ left: cropBox.x * 100 + '%', top: cropBox.y * 100 + '%', width: cropBox.w * 100 + '%', height: cropBox.h * 100 + '%' }"
            />
            <svg
              v-if="editMode === 'draw' && drawingPoints.length > 1"
              class="ink-live-overlay"
              viewBox="0 0 1 1"
              preserveAspectRatio="none"
            >
              <polyline
                :points="drawingPoints.map((p) => `${p.x},${p.y}`).join(' ')"
                fill="none"
                :stroke="inkColor"
                :stroke-width="inkThicknessRatio"
                stroke-linecap="round"
                stroke-linejoin="round"
              />
            </svg>
          </div>
          <p class="hint">
            <template v-if="editMode === 'crop'">미리보기 위를 드래그해 자를 영역을 선택하세요.</template>
            <template v-else-if="editMode === 'draw'">미리보기 위에 드래그해서 서명이나 그림을 그리세요.</template>
            <template v-else>지우고 싶은 획 근처를 클릭(드래그)하면 해당 획이 지워져요.</template>
          </p>

          <div class="controls">
            <div class="control-group">
              <span class="control-label">회전</span>
              <div class="btn-row">
                <button class="ghost-btn" @click="rotate(-90)">↺ 왼쪽</button>
                <button class="ghost-btn" @click="rotate(90)">↻ 오른쪽</button>
                <button class="ghost-btn" @click="resetCrop">자르기 초기화</button>
              </div>
            </div>

            <label class="control-group">
              <span class="control-label">
                자동 노출
                <input type="checkbox" :checked="selectedPage.autoExposure" @change="toggleAutoExposure" />
              </span>
            </label>

            <div class="control-group">
              <span class="control-label">밝기 <span class="mono">{{ selectedPage.brightness }}</span></span>
              <input type="range" min="-50" max="50" :value="selectedPage.brightness" @input="onAdjustInput('brightness', $event.target.value)" />
            </div>

            <div class="control-group">
              <span class="control-label">대비 <span class="mono">{{ selectedPage.contrast }}</span></span>
              <input type="range" min="-50" max="50" :value="selectedPage.contrast" @input="onAdjustInput('contrast', $event.target.value)" />
            </div>

            <div class="control-group">
              <span class="control-label">중간톤 <span class="mono">{{ selectedPage.midtones }}</span></span>
              <input type="range" min="-50" max="50" :value="selectedPage.midtones" @input="onAdjustInput('midtones', $event.target.value)" />
            </div>

            <div class="control-group meta">
              <span class="mono">{{ selectedPage.pageWidthPt.toFixed(1) }} × {{ selectedPage.pageHeightPt.toFixed(1) }} pt</span>
            </div>
          </div>
        </template>
        <p v-else class="hint">왼쪽에서 페이지를 선택하세요.</p>
      </main>

      <aside class="range-panel">
        <h2 class="range-title">범위 작업</h2>
        <p class="range-desc">몇 페이지부터 몇 페이지까지인지 입력하세요.</p>

        <div class="range-inputs">
          <label class="range-field">
            <span>시작</span>
            <input type="number" min="1" :max="pages.length" v-model="rangeFrom" />
          </label>
          <span class="range-sep">~</span>
          <label class="range-field">
            <span>끝</span>
            <input type="number" min="1" :max="pages.length" v-model="rangeTo" />
          </label>
        </div>

        <p v-if="rangeError" class="range-error">{{ rangeError }}</p>

        <div class="range-actions">
          <button class="ghost-btn danger-btn" :disabled="status.exporting" @click="deleteRange">
            범위 삭제
          </button>
          <button class="primary-btn" :disabled="status.exporting" @click="splitRange">
            범위 분리 다운로드
          </button>
        </div>
      </aside>
    </section>
  </div>
</template>

<style scoped>
.export-overlay {
  position: fixed;
  inset: 0;
  z-index: 1000;
  display: grid;
  place-items: center;
  background: rgba(27, 27, 31, 0.55);
  backdrop-filter: blur(2px);
}

.export-modal {
  width: 320px;
  max-width: 90vw;
  background: var(--paper-card);
  border-radius: 14px;
  padding: 28px 26px;
  text-align: center;
  box-shadow: 0 12px 40px rgba(0, 0, 0, 0.25);
}

.export-title {
  margin: 0 0 16px;
  font-family: var(--font-display);
  font-weight: 700;
  font-size: 17px;
}

.export-bar-track {
  width: 100%;
  height: 10px;
  border-radius: 999px;
  background: #efece3;
  overflow: hidden;
}

.export-bar-fill {
  height: 100%;
  background: var(--accent);
  border-radius: 999px;
  transition: width 0.2s ease;
}

.export-count {
  margin: 12px 0 4px;
  font-family: var(--font-mono);
  font-size: 13px;
  color: var(--ink-soft);
}

.export-hint {
  margin: 0;
  font-size: 12px;
  color: var(--ink-soft);
}

.workspace {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
}

.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 28px;
  background: var(--paper-card);
  border-bottom: 1px solid var(--line);
}

.brand {
  display: flex;
  align-items: center;
  gap: 12px;
}

.brand-mark {
  width: 38px;
  height: 38px;
  display: grid;
  place-items: center;
  background: var(--ink);
  color: var(--paper);
  font-family: var(--font-mono);
  font-weight: 600;
  font-size: 13px;
  border-radius: 8px;
}

.brand-text {
  display: flex;
  flex-direction: column;
  line-height: 1.15;
}

.brand-title {
  font-family: var(--font-display);
  font-weight: 700;
  font-size: 19px;
}

.brand-sub {
  font-size: 12px;
  color: var(--ink-soft);
}

.download-btn {
  text-decoration: none;
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.toolbar-status {
  display: flex;
  align-items: center;
  gap: 12px;
}

.page-count {
  font-family: var(--font-mono);
  font-size: 13px;
  color: var(--ink-soft);
}

.banner {
  margin: 0;
  padding: 10px 28px;
  background: var(--accent-soft);
  color: var(--accent);
  font-size: 14px;
}

.banner-stamp {
  background: var(--stamp-soft);
  color: var(--stamp);
}

.primary-btn {
  background: var(--ink);
  color: var(--paper);
  border: none;
  padding: 10px 18px;
  border-radius: 7px;
  font-weight: 600;
  font-size: 14px;
  cursor: pointer;
  transition: transform 0.12s ease, opacity 0.12s ease;
}

.primary-btn:hover:not(:disabled) {
  transform: translateY(-1px);
}

.primary-btn:disabled {
  opacity: 0.45;
  cursor: not-allowed;
}

.ghost-btn {
  background: transparent;
  border: 1px solid var(--line);
  color: var(--ink);
  padding: 9px 14px;
  border-radius: 7px;
  font-size: 13px;
  cursor: pointer;
}

.ghost-btn:hover {
  border-color: var(--accent);
  color: var(--accent);
}

.upload-btn {
  display: inline-flex;
  cursor: pointer;
}

.dropzone {
  flex: 1;
  display: grid;
  place-items: center;
  padding: 40px;
}

.dropzone-inner {
  max-width: 420px;
  text-align: center;
  background: var(--paper-card);
  border: 1px dashed var(--line);
  border-radius: 16px;
  padding: 48px 36px;
}

.dropzone-mark {
  display: inline-grid;
  place-items: center;
  width: 46px;
  height: 46px;
  border-radius: 50%;
  background: var(--accent-soft);
  color: var(--accent);
  font-size: 22px;
  margin-bottom: 16px;
}

.dropzone-inner h1 {
  font-family: var(--font-display);
  font-size: 22px;
  margin: 0 0 8px;
}

.dropzone-inner p {
  color: var(--ink-soft);
  font-size: 14px;
  margin: 0 0 22px;
}

.editor {
  flex: 1;
  display: grid;
  grid-template-columns: 300px 1fr 220px;
  min-height: 0;
}

.range-panel {
  border-left: 1px solid var(--line);
  padding: 18px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.range-title {
  margin: 0;
  font-family: var(--font-display);
  font-size: 15px;
  font-weight: 700;
}

.range-desc {
  margin: 0 0 6px;
  font-size: 12px;
  color: var(--ink-soft);
}

.range-inputs {
  display: flex;
  align-items: flex-end;
  gap: 8px;
}

.range-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  font-size: 12px;
  color: var(--ink-soft);
  flex: 1;
}

.range-field input[type='number'] {
  width: 100%;
  padding: 8px;
  border: 1px solid var(--line);
  border-radius: 6px;
  font-family: var(--font-mono);
  font-size: 14px;
}

.range-sep {
  padding-bottom: 9px;
  color: var(--ink-soft);
}

.range-error {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--stamp);
}

.range-actions {
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin-top: 10px;
}

.danger-btn:hover {
  border-color: var(--stamp);
  color: var(--stamp);
}

.page-tray {
  border-right: 1px solid var(--line);
  padding: 18px;
  overflow-y: auto;
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 12px;
  align-content: start;
}

.page-card {
  background: var(--paper-card);
  border: 2px solid transparent;
  border-radius: 10px;
  overflow: hidden;
  cursor: pointer;
  box-shadow: 0 1px 2px rgba(27, 27, 31, 0.08);
}

.page-card img {
  width: 100%;
  display: block;
  aspect-ratio: 3 / 4;
  object-fit: contain;
  background: #efece3;
}

.page-card.active {
  border-color: var(--accent);
}

.page-card-footer {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 8px;
}

.page-index {
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--ink-soft);
}

.icon-btn {
  border: none;
  background: transparent;
  cursor: pointer;
  font-size: 12px;
  color: var(--ink-soft);
  padding: 2px 6px;
  border-radius: 5px;
}

.icon-btn.danger:hover {
  background: var(--stamp-soft);
  color: var(--stamp);
}

.add-card {
  aspect-ratio: 3 / 4;
  display: grid;
  place-items: center;
  border: 1px dashed var(--line);
  color: var(--ink-soft);
  font-size: 13px;
  text-align: center;
  padding: 8px;
}

.detail-panel {
  padding: 24px 32px;
  overflow-y: auto;
}

.preview-frame {
  position: relative;
  max-width: 560px;
  margin: 0 auto;
  border-radius: 10px;
  overflow: hidden;
  background: #efece3;
  user-select: none;
  cursor: crosshair;
  box-shadow: 0 2px 10px rgba(27, 27, 31, 0.1);
}

.preview-frame.mode-draw {
  cursor: crosshair;
}

.preview-frame.mode-erase {
  cursor: not-allowed;
}

.ink-toolbar {
  max-width: 560px;
  margin: 0 auto 14px;
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
}

.mode-group {
  display: flex;
  gap: 6px;
}

.mode-btn {
  background: transparent;
  border: 1px solid var(--line);
  color: var(--ink);
  padding: 8px 12px;
  border-radius: 7px;
  font-size: 13px;
  cursor: pointer;
}

.mode-btn.active {
  background: var(--ink);
  color: var(--paper);
  border-color: var(--ink);
}

.ink-options {
  display: flex;
  align-items: center;
  gap: 8px;
}

.color-swatch {
  width: 22px;
  height: 22px;
  border-radius: 50%;
  border: 2px solid var(--line);
  cursor: pointer;
  padding: 0;
}

.color-swatch.active {
  border-color: var(--accent);
}

.ink-live-overlay {
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  pointer-events: none;
}

.preview-frame img {
  width: 100%;
  display: block;
  pointer-events: none;
}

.crop-box {
  position: absolute;
  border: 2px solid var(--accent);
  background: rgba(29, 111, 130, 0.15);
  pointer-events: none;
}

.hint {
  text-align: center;
  color: var(--ink-soft);
  font-size: 13px;
  margin-top: 10px;
}

.controls {
  max-width: 560px;
  margin: 24px auto 0;
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.control-group {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.control-label {
  font-size: 13px;
  font-weight: 600;
  display: flex;
  align-items: center;
  gap: 8px;
}

.mono {
  font-family: var(--font-mono);
  color: var(--ink-soft);
  font-weight: 400;
}

.btn-row {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
}

.control-group.meta {
  align-items: center;
}

input[type='range'] {
  accent-color: var(--accent);
}

@media (max-width: 1080px) {
  .editor {
    grid-template-columns: 300px 1fr;
  }
  .range-panel {
    border-left: none;
    border-top: 1px solid var(--line);
    grid-column: 1 / -1;
  }
}

@media (max-width: 860px) {
  .editor {
    grid-template-columns: 1fr;
  }
  .page-tray {
    border-right: none;
    border-bottom: 1px solid var(--line);
    max-height: 40vh;
  }
}
</style>
