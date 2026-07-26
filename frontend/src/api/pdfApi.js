const BASE_URL = import.meta.env.VITE_API_BASE_URL

/**
 * 백엔드 PdfController와 통신하는 얇은 API 레이어.
 * trial_id 쿠키를 주고받아야 하므로 모든 요청에 credentials: 'include' 필수.
 */
async function handleResponse(res) {
  if (!res.ok) {
    let message = `요청 실패 (${res.status})`
    try {
      const body = await res.json()
      if (body?.message) message = body.message
    } catch {
      // 응답 바디가 JSON이 아닐 수 있음(파일 스트림 등)
    }
    const error = new Error(message)
    error.status = res.status
    throw error
  }
  return res
}

export async function createSession(file) {
  const form = new FormData()
  form.append('file', file)
  const res = await fetch(`${BASE_URL}/pdf/sessions`, {
    method: 'POST',
    credentials: 'include',
    body: form,
  })
  return (await handleResponse(res)).json()
}

export async function insertFile(sessionId, file, afterDisplayIndex) {
  const form = new FormData()
  form.append('file', file)
  const query = afterDisplayIndex != null ? `?afterDisplayIndex=${afterDisplayIndex}` : ''
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/insert${query}`, {
    method: 'POST',
    credentials: 'include',
    body: form,
  })
  return (await handleResponse(res)).json()
}

export async function updatePage(sessionId, pageId, patch) {
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/pages/${pageId}`, {
    method: 'PATCH',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  })
  return (await handleResponse(res)).json()
}

export async function deletePage(sessionId, pageId) {
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/pages/${pageId}`, {
    method: 'DELETE',
    credentials: 'include',
  })
  return (await handleResponse(res)).json()
}

export async function reorderPages(sessionId, orderedPageIds) {
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/reorder`, {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ orderedPageIds }),
  })
  return (await handleResponse(res)).json()
}

export function renderPageUrl(sessionId, pageId, width, crop = true, cacheBust = 0) {
  return `${BASE_URL}/pdf/sessions/${sessionId}/pages/${pageId}/render?width=${width}&crop=${crop}&t=${cacheBust}`
}

export async function deletePageRange(sessionId, fromDisplayIndex, toDisplayIndex) {
  const res = await fetch(
    `${BASE_URL}/pdf/sessions/${sessionId}/pages/range?fromDisplayIndex=${fromDisplayIndex}&toDisplayIndex=${toDisplayIndex}`,
    { method: 'DELETE', credentials: 'include' },
  )
  return (await handleResponse(res)).json()
}

export async function startExport(sessionId, range) {
  const query = range ? `?fromDisplayIndex=${range.from}&toDisplayIndex=${range.to}` : ''
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/export/start${query}`, {
    method: 'POST',
    credentials: 'include',
  })
  return (await handleResponse(res)).json()
}

export async function getExportProgress(sessionId) {
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/export/progress`, {
    credentials: 'include',
  })
  return (await handleResponse(res)).json()
}

export async function downloadExportResult(sessionId) {
  const res = await fetch(`${BASE_URL}/pdf/sessions/${sessionId}/export/result`, {
    credentials: 'include',
  })
  await handleResponse(res)
  return res.blob()
}

/**
 * 데스크톱 앱 다운로드 버튼 클릭 시 카운트 기록용. 실패해도 사용자 다운로드 자체는
 * href 이동으로 이미 진행되므로, 호출부에서는 실패를 무시(fire-and-forget)해도 된다.
 */
export async function trackDownload(platform = 'desktop-windows') {
  const res = await fetch(`${BASE_URL}/download/track`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ platform }),
    keepalive: true,
  })
  return (await handleResponse(res)).json()
}

/**
 * 내보내기 시작 -> 완료될 때까지 진행률 폴링 -> 결과 blob 반환.
 * onProgress(completed, total)로 진행 상황을 콜백으로 알려준다.
 */
export async function exportSessionWithProgress(sessionId, onProgress, range) {
  const { total } = await startExport(sessionId, range)
  onProgress?.(0, total)

  while (true) {
    await new Promise((r) => setTimeout(r, 300))
    const progress = await getExportProgress(sessionId)
    onProgress?.(progress.completed, progress.total)

    if (progress.status === 'Done') {
      return downloadExportResult(sessionId)
    }
    if (progress.status === 'Error') {
      throw new Error(progress.error || '내보내기 중 오류가 발생했습니다.')
    }
  }
}
