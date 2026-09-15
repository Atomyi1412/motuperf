const DEFAULT_WINDOW_SECONDS = 180;
const MIN_WINDOW_SECONDS = 3;
const DRAG_THRESHOLD_PX = 8;
const WECHAT_APP = {
  bundle_id: "com.tencent.xin",
  name: "微信",
  version: "",
  recommended: true,
  reason: "微信宿主应用，测试微信小游戏默认选择它。",
};

const state = {
  samples: [],
  devices: [],
  apps: [],
  processes: [],
  status: null,
  selectedDevice: null,
  selectedApp: null,
  selectedProcess: null,
  running: false,
  timer: null,
  viewStartSec: null,
  viewEndSec: null,
  selectedTimeSec: null,
  followLatest: true,
  drag: null,
  chartAreas: {},
  imageCache: new Map(),
};

const el = {
  statusText: document.getElementById("statusText"),
  modeBadge: document.getElementById("modeBadge"),
  runningBadge: document.getElementById("runningBadge"),
  deviceSelect: document.getElementById("deviceSelect"),
  appSelect: document.getElementById("appSelect"),
  appSearchInput: document.getElementById("appSearchInput"),
  bundleInput: document.getElementById("bundleInput"),
  processSelect: document.getElementById("processSelect"),
  processSearchInput: document.getElementById("processSearchInput"),
  captureScreenshotsInput: document.getElementById("captureScreenshotsInput"),
  screenshotIntervalInput: document.getElementById("screenshotIntervalInput"),
  refreshDevicesBtn: document.getElementById("refreshDevicesBtn"),
  refreshAppsBtn: document.getElementById("refreshAppsBtn"),
  refreshProcessesBtn: document.getElementById("refreshProcessesBtn"),
  startBtn: document.getElementById("startBtn"),
  stopBtn: document.getElementById("stopBtn"),
  clearBtn: document.getElementById("clearBtn"),
  exportBtn: document.getElementById("exportBtn"),
  latestBtn: document.getElementById("latestBtn"),
  warnings: document.getElementById("warnings"),
  processHint: document.getElementById("processHint"),
  fpsValue: document.getElementById("fpsValue"),
  jankValue: document.getElementById("jankValue"),
  bigJankValue: document.getElementById("bigJankValue"),
  memoryValue: document.getElementById("memoryValue"),
  durationValue: document.getElementById("durationValue"),
  screenshotStatus: document.getElementById("screenshotStatus"),
  latestScreenshot: document.getElementById("latestScreenshot"),
  screenshotEmpty: document.getElementById("screenshotEmpty"),
  screenshotTimeline: document.getElementById("screenshotTimeline"),
  fpsChart: document.getElementById("fpsChart"),
  memoryChart: document.getElementById("memoryChart"),
  samplesBody: document.getElementById("samplesBody"),
  sampleCount: document.getElementById("sampleCount"),
};

async function api(path, options = {}) {
  const response = await fetch(path, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  const type = response.headers.get("content-type") || "";
  const payload = type.includes("application/json") ? await response.json() : await response.text();
  if (!response.ok) {
    throw new Error(payload.error || "请求失败");
  }
  return payload;
}

async function refresh() {
  const status = await api("/api/status");
  const samplePayload = await api("/api/sessions/current/samples");
  state.status = status;
  state.samples = normalizeSamples(samplePayload.samples || []);
  syncTimeState();
  renderStatus(status);
  renderLatestButton();
  renderMetrics(status);
  renderScreenshot(status);
  renderWarnings(status.warnings || []);
  renderCharts();
  renderTable();
}

function normalizeSamples(samples) {
  return samples
    .map((sample) => ({
      ...sample,
      elapsed_sec: Number(sample.elapsed_sec) || 0,
      fps: optionalNumber(sample.fps),
      jank: optionalNumber(sample.jank),
      big_jank: optionalNumber(sample.big_jank),
      memory_mb: optionalNumber(sample.memory_mb),
    }))
    .sort((left, right) => left.elapsed_sec - right.elapsed_sec);
}

function optionalNumber(value) {
  if (value === null || value === undefined || value === "") {
    return null;
  }
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

function syncTimeState() {
  const domain = getSampleDomain();
  if (!domain) {
    state.viewStartSec = null;
    state.viewEndSec = null;
    state.selectedTimeSec = null;
    state.followLatest = true;
    return;
  }

  if (state.selectedTimeSec === null || state.followLatest) {
    followLatestSample(false);
  } else {
    state.selectedTimeSec = clamp(state.selectedTimeSec, domain.start, domain.sampleEnd);
    if (state.viewStartSec === null || state.viewEndSec === null) {
      followLatestSample(false);
      state.followLatest = false;
      return;
    }
    setViewRange(state.viewStartSec, state.viewEndSec, false, false);
  }
}

function renderStatus(status) {
  const device = status.udid ? ` · 设备 ${status.udid.slice(-6)}` : "";
  const target = status.target_name ? `${status.target_name} · ${status.bundle_id}${device}` : `${status.bundle_id}${device}`;
  const captureText = status.capture_screenshots ? `截图 ${status.screenshot_interval_sec}s` : "未记录截图";
  el.statusText.textContent = `${target} · ${status.sample_count} samples · ${captureText}`;
  el.modeBadge.textContent = "iOS USB";
  el.modeBadge.className = "badge active";
  el.runningBadge.textContent = status.running ? "采集中" : "未采集";
  el.runningBadge.className = status.running ? "badge active" : "badge";
  el.startBtn.disabled = status.running;
  el.stopBtn.disabled = !status.running;
  if (document.activeElement !== el.captureScreenshotsInput) {
    el.captureScreenshotsInput.checked = Boolean(status.capture_screenshots);
  }
  if (document.activeElement !== el.screenshotIntervalInput) {
    el.screenshotIntervalInput.value = String(Math.round(Number(status.screenshot_interval_sec) || 10));
  }
}

function renderWarnings(warnings) {
  if (!warnings.length) {
    el.warnings.hidden = true;
    el.warnings.innerHTML = "";
    return;
  }
  el.warnings.hidden = false;
  el.warnings.innerHTML = `<ul>${warnings.map((item) => `<li>${escapeHtml(item)}</li>`).join("")}</ul>`;
}

async function refreshProcesses() {
  el.refreshProcessesBtn.disabled = true;
  try {
    const udid = encodeURIComponent(state.selectedDevice?.udid || "");
    const payload = await api(`/api/processes?udid=${udid}`);
    state.processes = payload.processes || [];
    renderProcesses();
  } catch (error) {
    renderWarnings([error.message]);
  } finally {
    el.refreshProcessesBtn.disabled = false;
  }
}

async function refreshDevices() {
  el.refreshDevicesBtn.disabled = true;
  try {
    const payload = await api("/api/devices");
    state.devices = payload.devices || [];
    renderDevices();
    await refreshApps();
    await refreshProcesses();
  } catch (error) {
    renderWarnings([error.message]);
  } finally {
    el.refreshDevicesBtn.disabled = false;
  }
}

async function refreshApps() {
  el.refreshAppsBtn.disabled = true;
  try {
    const udid = encodeURIComponent(state.selectedDevice?.udid || "");
    const payload = await api(`/api/apps?udid=${udid}`);
    state.apps = ensureKnownApps(payload.apps || []);
    renderApps();
  } catch (error) {
    renderWarnings([error.message]);
  } finally {
    el.refreshAppsBtn.disabled = false;
  }
}

function renderDevices() {
  const selected = el.deviceSelect.value;
  const options = state.devices.map((device) => {
    const label = formatDeviceLabel(device);
    return `<option value="${escapeHtml(device.udid)}">${escapeHtml(label)}</option>`;
  });
  el.deviceSelect.innerHTML = options.length ? options.join("") : '<option value="">未读取到设备</option>';

  const preferred =
    state.devices.find((device) => device.udid === selected) ||
    state.devices.find((device) => device.recommended) ||
    state.devices[0];
  if (preferred) {
    el.deviceSelect.value = preferred.udid;
    applySelectedDevice();
  } else {
    state.selectedDevice = null;
  }
}

function renderApps() {
  const selected = el.appSelect.value;
  const filteredApps = filterApps(state.apps, el.appSearchInput.value);
  const options = filteredApps.map((app) => {
    const label = formatAppLabel(app);
    return `<option value="${escapeHtml(app.bundle_id)}">${escapeHtml(label)}</option>`;
  });
  el.appSelect.innerHTML = options.length ? options.join("") : '<option value="">无匹配应用</option>';

  const preferred =
    filteredApps.find((app) => app.bundle_id === selected) ||
    filteredApps.find((app) => app.bundle_id === "com.tencent.xin") ||
    filteredApps.find((app) => app.bundle_id === el.bundleInput.value) ||
    filteredApps.find((app) => app.recommended) ||
    filteredApps[0];
  if (preferred) {
    el.appSelect.value = preferred.bundle_id;
    applySelectedApp();
  } else {
    state.selectedApp = null;
  }
}

function ensureKnownApps(apps) {
  const merged = [...apps];
  const index = merged.findIndex((app) => app.bundle_id === WECHAT_APP.bundle_id);
  if (index >= 0) {
    merged[index] = {
      ...merged[index],
      name: merged[index].name || WECHAT_APP.name,
      recommended: true,
      reason: merged[index].reason || WECHAT_APP.reason,
    };
  } else {
    merged.unshift(WECHAT_APP);
  }
  return merged;
}

function filterApps(apps, keyword) {
  const terms = searchTerms(keyword);
  if (!terms.length) {
    return apps;
  }
  return apps.filter((app) =>
    matchesTerms(
      [app.name, app.bundle_id, app.version, app.reason, app.recommended ? "推荐" : ""],
      terms,
    ),
  );
}

function renderProcesses() {
  const selected = el.processSelect.value;
  const filteredProcesses = filterProcesses(state.processes, el.processSearchInput.value);
  const options = filteredProcesses.map((process) => {
    const value = String(process.pid);
    const label = formatProcessLabel(process);
    return `<option value="${escapeHtml(value)}">${escapeHtml(label)}</option>`;
  });
  el.processSelect.innerHTML = options.length
    ? options.join("")
    : '<option value="">无匹配进程</option>';

  const preferred = choosePreferredProcess(selected, filteredProcesses);
  if (preferred) {
    el.processSelect.value = String(preferred.pid);
    applySelectedProcess();
  } else {
    state.selectedProcess = null;
    renderProcessHint();
  }
}

function choosePreferredProcess(previousValue, processes = state.processes) {
  if (previousValue) {
    const previous = processes.find((process) => String(process.pid) === previousValue);
    if (previous) {
      return previous;
    }
  }
  return (
    processes.find((process) => process.bundle_id && process.bundle_id === el.bundleInput.value) ||
    processes.find((process) => process.bundle_id === "com.tencent.xin") ||
    processes.find((process) => process.name === "WeChat") ||
    processes.find((process) => process.name === "com.apple.WebKit.WebContent") ||
    processes[0]
  );
}

function filterProcesses(processes, keyword) {
  const terms = searchTerms(keyword);
  if (!terms.length) {
    return processes;
  }
  return processes.filter((process) =>
    matchesTerms(
      [
        process.pid,
        process.name,
        process.bundle_id,
        process.display_name,
        process.reason,
        process.recommended ? "推荐" : "",
      ],
      terms,
    ),
  );
}

function formatProcessLabel(process) {
  const name = process.display_name || process.name || "unknown";
  const bundle = process.bundle_id || process.name;
  const mark = process.recommended ? "推荐" : "进程";
  return `${mark} · pid ${process.pid} · ${name} · ${bundle}`;
}

function formatDeviceLabel(device) {
  const name = device.market_name || device.name || "iOS Device";
  const version = device.product_version ? `iOS ${device.product_version}` : "iOS";
  const suffix = device.udid ? device.udid.slice(-6) : "";
  const conn = device.conn_type || "usb";
  return `${device.recommended ? "推荐 · " : ""}${name} · ${version} · ${conn} · ${suffix}`;
}

function formatAppLabel(app) {
  const name = app.name || app.bundle_id;
  const version = app.version ? ` · ${app.version}` : "";
  const mark = app.recommended ? "推荐 · " : "";
  return `${mark}${name} · ${app.bundle_id}${version}`;
}

function applySelectedDevice() {
  const selected = state.devices.find((device) => device.udid === el.deviceSelect.value);
  state.selectedDevice = selected || null;
}

function applySelectedApp() {
  const selected = state.apps.find((app) => app.bundle_id === el.appSelect.value);
  state.selectedApp = selected || null;
  if (selected) {
    el.bundleInput.value = selected.bundle_id;
    el.processSelect.value = "";
    renderProcesses();
  }
}

function applySelectedProcess() {
  const selected = state.processes.find((process) => String(process.pid) === el.processSelect.value);
  state.selectedProcess = selected || null;
  if (selected && selected.bundle_id) {
    el.bundleInput.value = selected.bundle_id;
  }
  renderProcessHint();
}

function renderProcessHint() {
  const process = state.selectedProcess;
  if (!process) {
    el.processHint.hidden = true;
    el.processHint.textContent = "";
    return;
  }

  const parts = [`当前候选：pid ${process.pid} · ${process.name}`];
  if (process.bundle_id) {
    parts.push(`Bundle ${process.bundle_id}`);
  } else {
    parts.push("无 Bundle，当前开源采集命令只能作为定位参考");
  }
  if (process.reason) {
    parts.push(process.reason);
  }
  el.processHint.hidden = false;
  el.processHint.textContent = parts.join(" · ");
}

function renderScreenshot(status) {
  const currentStatus = status || state.status || {};
  const selected = getSelectedSample();
  const selectedTime = selected ? selected.elapsed_sec : state.selectedTimeSec;
  const shot = selectedTime === null ? null : findNearestScreenshot(selectedTime);
  const latest = currentStatus.latest || state.samples[state.samples.length - 1] || {};
  const url = shot?.screenshot_url || latest.screenshot_url || currentStatus.latest_screenshot_url || "";
  const captureEnabled = Boolean(currentStatus.capture_screenshots);

  if (!url) {
    el.latestScreenshot.hidden = true;
    el.latestScreenshot.removeAttribute("src");
    delete el.latestScreenshot.dataset.url;
    el.screenshotEmpty.hidden = false;
    el.screenshotStatus.textContent = captureEnabled
      ? currentStatus.running
        ? currentStatus.latest_screenshot_error || "截图生成中"
        : "等待截图"
      : "未记录截图";
    return;
  }

  el.screenshotEmpty.hidden = true;
  el.latestScreenshot.hidden = false;
  if (el.latestScreenshot.dataset.url !== url) {
    el.latestScreenshot.src = `${url}?t=${Date.now()}`;
    el.latestScreenshot.dataset.url = url;
  }

  if (selected) {
    const shotText =
      shot && Math.abs(shot.elapsed_sec - selected.elapsed_sec) > 0.5
        ? ` · 截图 ${formatElapsed(shot.elapsed_sec)}`
        : "";
    el.screenshotStatus.textContent = `选中 ${formatElapsed(selected.elapsed_sec)} · ${formatClock(
      selected.timestamp,
    )}${shotText}`;
  } else {
    el.screenshotStatus.textContent = "最近截图";
  }
}

function renderMetrics(status) {
  const selected = getSelectedSample();
  const latest = status.latest || state.samples[state.samples.length - 1];
  const sample = selected || latest;
  if (!sample) {
    el.fpsValue.textContent = "--";
    el.jankValue.textContent = "--";
    el.bigJankValue.textContent = "--";
    el.memoryValue.textContent = "--";
    el.durationValue.textContent = "0s";
    return;
  }
  el.fpsValue.textContent = Number.isFinite(sample.fps) ? sample.fps.toFixed(1) : "--";
  el.jankValue.textContent = Number.isFinite(sample.jank) ? sample.jank : "--";
  el.bigJankValue.textContent = Number.isFinite(sample.big_jank) ? sample.big_jank : "--";
  el.memoryValue.textContent = Number.isFinite(sample.memory_mb) ? `${sample.memory_mb.toFixed(0)} MB` : "--";
  const elapsed = Number(sample.elapsed_sec);
  el.durationValue.textContent = formatElapsed(Number.isFinite(elapsed) ? elapsed : status.elapsed_sec || 0);
}

function renderCharts() {
  drawFrameChart();
  drawMemoryChart();
  drawScreenshotTimeline();
}

function drawFrameChart() {
  const ctx = el.fpsChart.getContext("2d");
  const width = el.fpsChart.width;
  const height = el.fpsChart.height;
  const visible = getVisibleSamples();
  const fpsAxis = computeAxisRange(
    visible.map((sample) => sample.fps),
    { defaultMin: 0, defaultMax: 60, minFloor: 0, minSpan: 10 },
  );
  const jankAxis = computeAxisRange(
    visible.flatMap((sample) => [sample.jank, sample.big_jank]),
    { defaultMin: 0, defaultMax: 8, forceMinZero: true, minSpan: 4, minimumMax: 4 },
  );
  const area = clearChart(ctx, width, height, {
    left: { title: "FPS", color: "#f47c9d", ...fpsAxis },
    right: { title: "Jank / BigJank 次", color: "#9ed7ec", ...jankAxis },
  });
  state.chartAreas[el.fpsChart.id] = area;
  if (!state.samples.length) {
    drawEmpty(ctx, area);
    return;
  }
  drawLine(ctx, visible, { key: "fps", color: "#f47c9d", axis: "left" }, area);
  drawLine(ctx, visible, { key: "jank", color: "#9ed7ec", axis: "right" }, area);
  drawLine(ctx, visible, { key: "big_jank", color: "#b9d986", axis: "right" }, area);
  drawSelectionCursor(ctx, area);
  drawDragSelection(ctx, area);
}

function drawMemoryChart() {
  const ctx = el.memoryChart.getContext("2d");
  const width = el.memoryChart.width;
  const height = el.memoryChart.height;
  const visible = getVisibleSamples();
  const memoryAxis = computeAxisRange(
    visible.map((sample) => sample.memory_mb),
    { defaultMin: 0, defaultMax: 1000, minFloor: 0, minSpan: 50 },
  );
  const area = clearChart(ctx, width, height, {
    left: { title: "MB", color: "#d6788d", ...memoryAxis },
  });
  state.chartAreas[el.memoryChart.id] = area;
  if (!state.samples.length) {
    drawEmpty(ctx, area);
    return;
  }
  drawLine(ctx, visible, { key: "memory_mb", color: "#d6788d", axis: "left" }, area);
  drawSelectionCursor(ctx, area);
  drawDragSelection(ctx, area);
}

function drawScreenshotTimeline() {
  const ctx = el.screenshotTimeline.getContext("2d");
  const width = el.screenshotTimeline.width;
  const height = el.screenshotTimeline.height;
  const area = clearTimelineChart(ctx, width, height);
  state.chartAreas[el.screenshotTimeline.id] = area;
  if (!state.samples.length) {
    drawEmpty(ctx, area);
    return;
  }

  const shots = state.samples.filter((sample) => sample.screenshot_url && isInView(sample.elapsed_sec));
  const selectedShot = state.selectedTimeSec === null ? null : findNearestScreenshot(state.selectedTimeSec);
  const thumbnailSamples = chooseThumbnailSamples(shots, selectedShot, area);

  ctx.strokeStyle = "rgba(244,245,247,0.28)";
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(area.left, area.bottom - 18);
  ctx.lineTo(area.right, area.bottom - 18);
  ctx.stroke();

  for (const shot of shots) {
    const x = timeToX(shot.elapsed_sec, area);
    ctx.strokeStyle = shot === selectedShot ? "#4fb7ff" : "rgba(244,245,247,0.32)";
    ctx.lineWidth = shot === selectedShot ? 3 : 1;
    ctx.beginPath();
    ctx.moveTo(x, area.bottom - 32);
    ctx.lineTo(x, area.bottom - 4);
    ctx.stroke();
  }

  for (const shot of thumbnailSamples) {
    drawScreenshotThumbnail(ctx, shot, selectedShot, area);
  }

  drawSelectionCursor(ctx, area);
  drawDragSelection(ctx, area);
}

function clearChart(ctx, width, height, axes) {
  const area = getChartArea(width, height);
  const view = getViewRange();
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = "#171a23";
  ctx.fillRect(0, 0, width, height);
  ctx.strokeStyle = "rgba(255,255,255,0.12)";
  ctx.lineWidth = 1;
  ctx.font = "16px Segoe UI, Microsoft YaHei, Arial";

  drawAxisTitle(ctx, axes.left, area.left, 24, "left");
  if (axes.right) {
    drawAxisTitle(ctx, axes.right, area.right, 24, "right");
  }

  const ticks = 4;
  for (let i = 0; i <= ticks; i += 1) {
    const y = area.top + (area.height * i) / ticks;
    ctx.beginPath();
    ctx.moveTo(area.left, y);
    ctx.lineTo(area.right, y);
    ctx.stroke();

    drawAxisTick(ctx, axes.left, area.left - 12, y, i, ticks, "right");
    if (axes.right) {
      drawAxisTick(ctx, axes.right, area.right + 12, y, i, ticks, "left");
    }
  }

  drawTimeGrid(ctx, area, view);
  drawChartFrame(ctx, area, Boolean(axes.right));
  return { ...area, axes, view };
}

function clearTimelineChart(ctx, width, height) {
  const area = getChartArea(width, height);
  const view = getViewRange();
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = "#171a23";
  ctx.fillRect(0, 0, width, height);
  ctx.font = "16px Segoe UI, Microsoft YaHei, Arial";
  ctx.fillStyle = "#4fb7ff";
  ctx.textAlign = "left";
  ctx.textBaseline = "middle";
  ctx.fillText("截图", area.left, 24);
  ctx.strokeStyle = "rgba(255,255,255,0.12)";
  ctx.lineWidth = 1;
  drawTimeGrid(ctx, area, view);
  drawChartFrame(ctx, area, false);
  return { ...area, axes: {}, view };
}

function getChartArea(width, height) {
  const left = 76;
  const rightPadding = 96;
  const top = 42;
  const bottomPadding = 48;
  return {
    left,
    top,
    right: width - rightPadding,
    bottom: height - bottomPadding,
    width: width - left - rightPadding,
    height: height - top - bottomPadding,
  };
}

function drawChartFrame(ctx, area, hasRightAxis) {
  ctx.strokeStyle = "rgba(255,255,255,0.26)";
  ctx.lineWidth = 1;
  ctx.beginPath();
  ctx.moveTo(area.left, area.top);
  ctx.lineTo(area.left, area.bottom);
  ctx.lineTo(area.right, area.bottom);
  if (hasRightAxis) {
    ctx.moveTo(area.right, area.top);
    ctx.lineTo(area.right, area.bottom);
  }
  ctx.stroke();
}

function drawTimeGrid(ctx, area, view) {
  const ticks = 6;
  ctx.textAlign = "center";
  ctx.textBaseline = "top";
  ctx.fillStyle = "rgba(244,245,247,0.72)";
  ctx.font = "13px Segoe UI, Microsoft YaHei, Arial";
  for (let i = 0; i <= ticks; i += 1) {
    const ratio = i / ticks;
    const x = area.left + area.width * ratio;
    const time = view.start + (view.end - view.start) * ratio;
    ctx.strokeStyle = "rgba(255,255,255,0.12)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(x, area.top);
    ctx.lineTo(x, area.bottom);
    ctx.stroke();
    ctx.fillText(formatElapsed(time), x, area.bottom + 12);
  }
}

function drawAxisTitle(ctx, axis, x, y, align) {
  ctx.textAlign = align;
  ctx.textBaseline = "middle";
  ctx.fillStyle = axis.color;
  ctx.font = "16px Segoe UI, Microsoft YaHei, Arial";
  ctx.fillText(axis.title, x, y);
}

function drawAxisTick(ctx, axis, x, y, index, ticks, align) {
  const value = axis.max - ((axis.max - axis.min) * index) / ticks;
  ctx.textAlign = align;
  ctx.textBaseline = "middle";
  ctx.fillStyle = "rgba(244,245,247,0.72)";
  ctx.font = "14px Segoe UI, Microsoft YaHei, Arial";
  ctx.fillText(formatTickValue(value), x, y);
}

function drawEmpty(ctx, area) {
  ctx.fillStyle = "rgba(244,245,247,0.48)";
  ctx.font = "20px Microsoft YaHei, Segoe UI, Arial";
  ctx.textAlign = "center";
  ctx.textBaseline = "middle";
  ctx.fillText("点击开始采集后显示实时曲线", area.left + area.width / 2, area.top + area.height / 2);
  ctx.textAlign = "left";
}

function drawLine(ctx, samples, spec, area) {
  if (!samples.length) {
    return;
  }
  const axis = area.axes[spec.axis];
  ctx.strokeStyle = spec.color;
  ctx.lineWidth = spec.key === "fps" || spec.key === "memory_mb" ? 3 : 2;
  ctx.beginPath();
  let drawn = false;
  for (const sample of samples) {
    const rawValue = sample[spec.key];
    const value = rawValue === null || rawValue === undefined ? Number.NaN : Number(rawValue);
    if (!Number.isFinite(value)) {
      drawn = false;
      continue;
    }
    const x = timeToX(sample.elapsed_sec, area);
    const y = valueToY(value, axis, area);
    if (!drawn) {
      ctx.moveTo(x, y);
      drawn = true;
    } else {
      ctx.lineTo(x, y);
    }
  }
  ctx.stroke();

  if (samples.length === 1) {
    const value = Number(samples[0][spec.key]);
    ctx.fillStyle = spec.color;
    ctx.beginPath();
    ctx.arc(timeToX(samples[0].elapsed_sec, area), valueToY(value, axis, area), 4, 0, Math.PI * 2);
    ctx.fill();
  }
}

function drawSelectionCursor(ctx, area) {
  if (state.selectedTimeSec === null || !isInView(state.selectedTimeSec)) {
    return;
  }
  const x = timeToX(state.selectedTimeSec, area);
  ctx.strokeStyle = "rgba(79,183,255,0.95)";
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(x, area.top);
  ctx.lineTo(x, area.bottom);
  ctx.stroke();

  ctx.fillStyle = "rgba(79,183,255,0.16)";
  ctx.beginPath();
  ctx.arc(x, area.top + 10, 5, 0, Math.PI * 2);
  ctx.fill();
}

function drawDragSelection(ctx, area) {
  if (!state.drag || state.drag.mode !== "select") {
    return;
  }
  const start = Math.min(state.drag.startTime, state.drag.currentTime);
  const end = Math.max(state.drag.startTime, state.drag.currentTime);
  if (Math.abs(end - start) <= 0) {
    return;
  }
  const x1 = clamp(timeToX(start, area), area.left, area.right);
  const x2 = clamp(timeToX(end, area), area.left, area.right);
  ctx.fillStyle = "rgba(79,183,255,0.14)";
  ctx.strokeStyle = "rgba(79,183,255,0.75)";
  ctx.lineWidth = 1;
  ctx.fillRect(x1, area.top, x2 - x1, area.height);
  ctx.strokeRect(x1, area.top, x2 - x1, area.height);
}

function drawScreenshotThumbnail(ctx, shot, selectedShot, area) {
  const x = timeToX(shot.elapsed_sec, area);
  const width = 58;
  const height = 118;
  const left = clamp(x - width / 2, area.left, area.right - width);
  const top = area.top + 18;
  const isSelected = shot === selectedShot;
  ctx.fillStyle = "#11151f";
  ctx.strokeStyle = isSelected ? "#4fb7ff" : "rgba(244,245,247,0.26)";
  ctx.lineWidth = isSelected ? 3 : 1;
  roundRect(ctx, left, top, width, height, 6);
  ctx.fill();
  ctx.stroke();

  const cached = getCachedImage(shot.screenshot_url);
  if (cached.loaded) {
    drawImageContain(ctx, cached.image, left + 4, top + 4, width - 8, height - 8);
    return;
  }

  ctx.fillStyle = "rgba(244,245,247,0.18)";
  ctx.fillRect(left + 8, top + 8, width - 16, height - 16);
}

function chooseThumbnailSamples(shots, selectedShot, area) {
  const chosen = [];
  let lastX = -Infinity;
  for (const shot of shots) {
    const x = timeToX(shot.elapsed_sec, area);
    if (x - lastX >= 78) {
      chosen.push(shot);
      lastX = x;
    }
  }
  if (selectedShot && isInView(selectedShot.elapsed_sec) && !chosen.includes(selectedShot)) {
    chosen.push(selectedShot);
  }
  return chosen.sort((left, right) => left.elapsed_sec - right.elapsed_sec);
}

function getCachedImage(url) {
  let cached = state.imageCache.get(url);
  if (cached) {
    return cached;
  }
  const image = new Image();
  cached = { image, loaded: false, failed: false };
  image.onload = () => {
    cached.loaded = true;
    renderCharts();
  };
  image.onerror = () => {
    cached.failed = true;
  };
  image.src = url;
  state.imageCache.set(url, cached);
  return cached;
}

function drawImageContain(ctx, image, x, y, width, height) {
  const scale = Math.min(width / image.naturalWidth, height / image.naturalHeight);
  const drawWidth = image.naturalWidth * scale;
  const drawHeight = image.naturalHeight * scale;
  const drawX = x + (width - drawWidth) / 2;
  const drawY = y + (height - drawHeight) / 2;
  ctx.drawImage(image, drawX, drawY, drawWidth, drawHeight);
}

function roundRect(ctx, x, y, width, height, radius) {
  ctx.beginPath();
  ctx.moveTo(x + radius, y);
  ctx.lineTo(x + width - radius, y);
  ctx.quadraticCurveTo(x + width, y, x + width, y + radius);
  ctx.lineTo(x + width, y + height - radius);
  ctx.quadraticCurveTo(x + width, y + height, x + width - radius, y + height);
  ctx.lineTo(x + radius, y + height);
  ctx.quadraticCurveTo(x, y + height, x, y + height - radius);
  ctx.lineTo(x, y + radius);
  ctx.quadraticCurveTo(x, y, x + radius, y);
  ctx.closePath();
}

function computeAxisRange(values, options) {
  const finite = values.filter((value) => Number.isFinite(value));
  if (!finite.length) {
    return { min: options.defaultMin, max: options.defaultMax };
  }

  let min = Math.min(...finite);
  let max = Math.max(...finite);
  const minSpan = options.minSpan || 1;

  if (options.forceMinZero) {
    min = 0;
    max = Math.max(max, options.minimumMax || minSpan);
  } else {
    const span = max - min;
    if (span < minSpan) {
      const center = (max + min) / 2;
      min = center - minSpan / 2;
      max = center + minSpan / 2;
    } else {
      const padding = span * 0.14;
      min -= padding;
      max += padding;
    }
    if (options.minFloor !== undefined) {
      min = Math.max(options.minFloor, min);
    }
  }

  return niceAxisBounds(min, max, Boolean(options.forceMinZero));
}

function niceAxisBounds(min, max, forceMinZero) {
  const span = Math.max(max - min, 1);
  const step = niceStep(span / 4);
  const niceMin = forceMinZero ? 0 : Math.floor(min / step) * step;
  let niceMax = Math.ceil(max / step) * step;
  if (niceMax <= niceMin) {
    niceMax = niceMin + step;
  }
  return { min: niceMin, max: niceMax };
}

function niceStep(rawStep) {
  if (!Number.isFinite(rawStep) || rawStep <= 0) {
    return 1;
  }
  const power = 10 ** Math.floor(Math.log10(rawStep));
  const unit = rawStep / power;
  if (unit <= 1) {
    return power;
  }
  if (unit <= 2) {
    return power * 2;
  }
  if (unit <= 5) {
    return power * 5;
  }
  return power * 10;
}

function renderTable() {
  const selected = getSelectedSample();
  el.sampleCount.textContent = `${state.samples.length} samples`;
  const rows = state.samples
    .slice(-12)
    .reverse()
    .map((sample) => {
      const selectedClass = selected && sample.elapsed_sec === selected.elapsed_sec ? ' class="selected-row"' : "";
      return `
      <tr data-time="${sample.elapsed_sec}"${selectedClass}>
        <td>${escapeHtml(sample.timestamp)}</td>
        <td>${Number(sample.elapsed_sec).toFixed(1)}</td>
        <td>${formatOptionalMetric(sample.fps, 1)}</td>
        <td>${formatOptionalMetric(sample.jank, 0)}</td>
        <td>${formatOptionalMetric(sample.big_jank, 0)}</td>
        <td>${formatOptionalMetric(sample.memory_mb, 1)}</td>
        <td>${renderScreenshotLink(sample.screenshot_url)}</td>
        <td>${escapeHtml(sample.source)}</td>
        <td>${escapeHtml(sample.note || "")}</td>
      </tr>
    `;
    })
    .join("");
  el.samplesBody.innerHTML = rows;
}

function formatOptionalMetric(value, digits) {
  return Number.isFinite(value) ? Number(value).toFixed(digits) : "--";
}

function renderScreenshotLink(url) {
  if (!url) {
    return "--";
  }
  return `<a href="${escapeHtml(url)}" target="_blank" rel="noreferrer">查看</a>`;
}

async function startSession() {
  const selected = state.selectedProcess;
  try {
    await api("/api/sessions/start", {
      method: "POST",
      body: JSON.stringify({
        bundle_id: el.bundleInput.value || "com.tencent.xin",
        mode: "ios",
        udid: state.selectedDevice?.udid || "",
        target_pid: selected ? selected.pid : null,
        target_name: selected ? selected.name : "",
        capture_screenshots: el.captureScreenshotsInput.checked,
        screenshot_interval_sec: Number(el.screenshotIntervalInput.value) || 10,
      }),
    });
    state.followLatest = true;
    beginPolling();
    await refresh();
  } catch (error) {
    renderWarnings([error.message]);
  }
}

async function syncCaptureOptions() {
  try {
    await api("/api/sessions/options", {
      method: "POST",
      body: JSON.stringify({
        capture_screenshots: el.captureScreenshotsInput.checked,
        screenshot_interval_sec: Number(el.screenshotIntervalInput.value) || 10,
      }),
    });
    await refresh();
  } catch (error) {
    renderWarnings([error.message]);
  }
}

async function stopSession() {
  await api("/api/sessions/stop", { method: "POST", body: "{}" });
  await refresh();
}

async function clearSession() {
  await api("/api/sessions/clear", { method: "POST", body: "{}" });
  state.viewStartSec = null;
  state.viewEndSec = null;
  state.selectedTimeSec = null;
  state.followLatest = true;
  await refresh();
}

function exportCsv() {
  window.location.href = "/api/sessions/current/export.csv";
}

function beginPolling() {
  if (state.timer) {
    return;
  }
  state.timer = window.setInterval(() => {
    refresh().catch((error) => renderWarnings([error.message]));
  }, 1000);
}

function getVisibleSamples() {
  return state.samples.filter((sample) => isInView(sample.elapsed_sec));
}

function getSelectedSample() {
  if (!state.samples.length || state.selectedTimeSec === null) {
    return null;
  }
  return findNearestSample(state.selectedTimeSec);
}

function findNearestSample(time) {
  return findNearestByTime(state.samples, time);
}

function findNearestScreenshot(time) {
  return findNearestByTime(
    state.samples.filter((sample) => sample.screenshot_url),
    time,
  );
}

function findNearestByTime(samples, time) {
  if (!samples.length || time === null || !Number.isFinite(time)) {
    return null;
  }
  let nearest = samples[0];
  let nearestDistance = Math.abs(samples[0].elapsed_sec - time);
  for (const sample of samples.slice(1)) {
    const distance = Math.abs(sample.elapsed_sec - time);
    if (distance < nearestDistance) {
      nearest = sample;
      nearestDistance = distance;
    }
  }
  return nearest;
}

function getSampleDomain() {
  if (!state.samples.length) {
    return null;
  }
  const last = state.samples[state.samples.length - 1].elapsed_sec;
  return {
    start: 0,
    end: Math.max(1, last),
    sampleEnd: last,
  };
}

function getCurrentWindowSpan() {
  if (state.viewStartSec === null || state.viewEndSec === null) {
    return DEFAULT_WINDOW_SECONDS;
  }
  return Math.max(MIN_WINDOW_SECONDS, state.viewEndSec - state.viewStartSec);
}

function getViewRange() {
  if (state.viewStartSec !== null && state.viewEndSec !== null && state.viewEndSec > state.viewStartSec) {
    return { start: state.viewStartSec, end: state.viewEndSec };
  }
  const domain = getSampleDomain();
  if (domain) {
    return { start: domain.start, end: domain.end };
  }
  return { start: 0, end: DEFAULT_WINDOW_SECONDS };
}

function setViewRange(start, end, followLatest = false, shouldRender = true) {
  const domain = getSampleDomain();
  if (!domain) {
    return;
  }
  let nextStart = Math.min(start, end);
  let nextEnd = Math.max(start, end);
  let span = nextEnd - nextStart;
  const fullSpan = domain.end - domain.start;

  if (span < MIN_WINDOW_SECONDS && fullSpan >= MIN_WINDOW_SECONDS) {
    const center = (nextStart + nextEnd) / 2;
    span = MIN_WINDOW_SECONDS;
    nextStart = center - span / 2;
    nextEnd = center + span / 2;
  }

  if (span >= fullSpan) {
    nextStart = domain.start;
    nextEnd = domain.end;
  } else {
    if (nextStart < domain.start) {
      nextEnd += domain.start - nextStart;
      nextStart = domain.start;
    }
    if (nextEnd > domain.end) {
      nextStart -= nextEnd - domain.end;
      nextEnd = domain.end;
    }
  }

  state.viewStartSec = nextStart;
  state.viewEndSec = nextEnd;
  state.followLatest = followLatest;
  if (shouldRender) {
    renderLatestButton();
    renderMetrics({ elapsed_sec: domain.sampleEnd });
    renderScreenshot({ ...(state.status || {}), latest: state.samples[state.samples.length - 1] });
    renderCharts();
    renderTable();
  }
}

function resetTimeView() {
  followLatestSample(true);
}

function followLatestSample(shouldRender = true) {
  const domain = getSampleDomain();
  if (!domain) {
    return;
  }
  state.viewEndSec = domain.end;
  state.viewStartSec = 0;
  state.selectedTimeSec = domain.sampleEnd;
  state.followLatest = true;
  if (shouldRender) {
    renderLatestButton();
    renderMetrics({ elapsed_sec: domain.sampleEnd });
    renderScreenshot({ ...(state.status || {}), latest: state.samples[state.samples.length - 1] });
    renderCharts();
    renderTable();
  }
}

function renderLatestButton() {
  el.latestBtn.textContent = state.followLatest ? "跟随最新" : "定位最新";
  el.latestBtn.classList.toggle("active", state.followLatest);
}

function selectTime(time, keepVisible = true) {
  const sample = findNearestSample(time);
  if (!sample) {
    return;
  }
  state.selectedTimeSec = sample.elapsed_sec;
  state.followLatest = false;
  if (keepVisible && !isInView(sample.elapsed_sec)) {
    centerViewOn(sample.elapsed_sec);
  }
  renderMetrics({ elapsed_sec: sample.elapsed_sec });
  renderScreenshot({ ...(state.status || {}), latest: sample });
  renderCharts();
  renderTable();
}

function centerViewOn(time) {
  const view = getViewRange();
  const span = view.end - view.start;
  setViewRange(time - span / 2, time + span / 2, false, false);
}

function isInView(time) {
  const view = getViewRange();
  return time >= view.start && time <= view.end;
}

function timeToX(time, area) {
  const view = area.view || getViewRange();
  const span = Math.max(view.end - view.start, 0.001);
  return area.left + ((time - view.start) / span) * area.width;
}

function xToTime(x, area) {
  const view = area.view || getViewRange();
  const ratio = clamp((x - area.left) / area.width, 0, 1);
  return view.start + (view.end - view.start) * ratio;
}

function valueToY(value, axis, area) {
  const span = Math.max(axis.max - axis.min, 0.001);
  return area.top + area.height - ((value - axis.min) / span) * area.height;
}

function handlePointerDown(event) {
  if ((event.button !== undefined && event.button !== 0) || !state.samples.length) {
    return;
  }
  event.preventDefault();
  const info = getPointerChartInfo(event);
  if (!info) {
    return;
  }
  const mode = event.shiftKey || info.y > info.area.bottom ? "pan" : "select";
  state.drag = {
    canvas: event.currentTarget,
    pointerId: event.pointerId,
    mode,
    startX: info.x,
    currentX: info.x,
    startTime: xToTime(info.x, info.area),
    currentTime: xToTime(info.x, info.area),
    viewStart: state.viewStartSec,
    viewEnd: state.viewEndSec,
    moved: false,
  };
  if (event.currentTarget.setPointerCapture && event.pointerId !== undefined) {
    event.currentTarget.setPointerCapture(event.pointerId);
  }
}

function handlePointerMove(event) {
  if (!state.drag || (state.drag.pointerId !== undefined && state.drag.pointerId !== event.pointerId)) {
    return;
  }
  event.preventDefault();
  const info = getPointerChartInfo(event, state.drag.canvas);
  if (!info) {
    return;
  }
  state.drag.currentX = info.x;
  state.drag.currentTime = xToTime(info.x, info.area);
  state.drag.moved = state.drag.moved || Math.abs(state.drag.currentX - state.drag.startX) >= DRAG_THRESHOLD_PX;

  if (state.drag.mode === "pan") {
    const span = state.drag.viewEnd - state.drag.viewStart;
    const delta = ((state.drag.startX - state.drag.currentX) / info.area.width) * span;
    setViewRange(state.drag.viewStart + delta, state.drag.viewEnd + delta, false, true);
    return;
  }

  renderCharts();
}

function handlePointerUp(event) {
  if (!state.drag || (state.drag.pointerId !== undefined && state.drag.pointerId !== event.pointerId)) {
    return;
  }
  event.preventDefault();
  const drag = state.drag;
  state.drag = null;
  if (!drag.moved) {
    selectTime(drag.currentTime);
    renderCharts();
    return;
  }

  if (drag.mode === "select") {
    const start = Math.min(drag.startTime, drag.currentTime);
    const end = Math.max(drag.startTime, drag.currentTime);
    if (end - start >= MIN_WINDOW_SECONDS) {
      setViewRange(start, end, false, false);
      selectTime((start + end) / 2, false);
      return;
    }
  }
  renderCharts();
}

function handlePointerCancel(event) {
  if (!state.drag || (state.drag.pointerId !== undefined && state.drag.pointerId !== event.pointerId)) {
    return;
  }
  state.drag = null;
  renderCharts();
}

function handleWheel(event) {
  if (!state.samples.length) {
    return;
  }
  event.preventDefault();
  const info = getPointerChartInfo(event);
  if (!info) {
    return;
  }
  const view = getViewRange();
  const span = view.end - view.start;
  if (event.shiftKey) {
    const delta = (event.deltaY / 800) * span;
    setViewRange(view.start + delta, view.end + delta, false);
    return;
  }

  const anchor = xToTime(info.x, info.area);
  const zoomFactor = event.deltaY > 0 ? 1.18 : 0.82;
  const nextSpan = span * zoomFactor;
  const leftRatio = (anchor - view.start) / span;
  setViewRange(anchor - nextSpan * leftRatio, anchor + nextSpan * (1 - leftRatio), false);
}

function getPointerChartInfo(event, canvas = event.currentTarget) {
  const area = state.chartAreas[canvas.id];
  if (!area) {
    return null;
  }
  const rect = canvas.getBoundingClientRect();
  const x = ((event.clientX - rect.left) / rect.width) * canvas.width;
  const y = ((event.clientY - rect.top) / rect.height) * canvas.height;
  return { area, x, y };
}

function formatTickValue(value) {
  if (Math.abs(value) >= 100) {
    return String(Math.round(value));
  }
  if (Number.isInteger(value)) {
    return String(value);
  }
  return value.toFixed(1);
}

function formatElapsed(seconds) {
  const safeSeconds = Math.max(0, Number(seconds) || 0);
  const minutes = Math.floor(safeSeconds / 60);
  const secs = Math.floor(safeSeconds % 60);
  const tenths = Math.floor((safeSeconds % 1) * 10);
  if (minutes > 0) {
    return `${minutes}:${String(secs).padStart(2, "0")}`;
  }
  return `${secs}.${tenths}s`;
}

function formatClock(timestamp) {
  if (!timestamp) {
    return "--";
  }
  const date = new Date(timestamp);
  if (Number.isNaN(date.getTime())) {
    return timestamp;
  }
  return date.toLocaleTimeString("zh-CN", { hour12: false });
}

function clamp(value, min, max) {
  return Math.max(min, Math.min(max, value));
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;");
}

function searchTerms(keyword) {
  return String(keyword || "")
    .trim()
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean);
}

function matchesTerms(values, terms) {
  const haystack = values.map((value) => String(value || "").toLowerCase()).join(" ");
  return terms.every((term) => haystack.includes(term));
}

for (const canvas of [el.fpsChart, el.memoryChart, el.screenshotTimeline]) {
  if ("PointerEvent" in window) {
    canvas.addEventListener("pointerdown", handlePointerDown);
    canvas.addEventListener("pointermove", handlePointerMove);
    canvas.addEventListener("pointerup", handlePointerUp);
    canvas.addEventListener("pointercancel", handlePointerCancel);
  } else {
    canvas.addEventListener("mousedown", handlePointerDown);
    canvas.addEventListener("mousemove", handlePointerMove);
    canvas.addEventListener("mouseup", handlePointerUp);
    canvas.addEventListener("mouseleave", handlePointerCancel);
  }
  canvas.addEventListener("dblclick", resetTimeView);
  canvas.addEventListener("wheel", handleWheel, { passive: false });
}

el.startBtn.addEventListener("click", startSession);
el.stopBtn.addEventListener("click", stopSession);
el.clearBtn.addEventListener("click", clearSession);
el.exportBtn.addEventListener("click", exportCsv);
el.latestBtn.addEventListener("click", () => followLatestSample(true));
el.refreshDevicesBtn.addEventListener("click", refreshDevices);
el.refreshAppsBtn.addEventListener("click", refreshApps);
el.refreshProcessesBtn.addEventListener("click", refreshProcesses);
el.appSearchInput.addEventListener("input", renderApps);
el.processSearchInput.addEventListener("input", renderProcesses);
el.captureScreenshotsInput.addEventListener("change", syncCaptureOptions);
el.screenshotIntervalInput.addEventListener("change", syncCaptureOptions);
el.deviceSelect.addEventListener("change", async () => {
  applySelectedDevice();
  await refreshApps();
  await refreshProcesses();
});
el.appSelect.addEventListener("change", applySelectedApp);
el.processSelect.addEventListener("change", applySelectedProcess);
el.samplesBody.addEventListener("click", (event) => {
  const row = event.target.closest("tr[data-time]");
  if (row) {
    selectTime(Number(row.dataset.time));
  }
});

beginPolling();
refresh().catch((error) => renderWarnings([error.message]));
refreshDevices();
