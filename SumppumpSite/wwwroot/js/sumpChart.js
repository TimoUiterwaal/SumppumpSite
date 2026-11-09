// The level chart on the Home page. Home.razor calls these functions through JS interop.
// Chart.js, its date adapter, Hammer.js and the zoom plugin are loaded by App.razor.

let chart = null;
let dotnet = null;          // DotNetObjectReference to the Home component
let marks = [];
let bucketSec = 0;
let zoomed = false;
let ignoreZoom = false;
let C = {};

function readColors(el) {
  const s = getComputedStyle(el), v = n => s.getPropertyValue(n).trim();
  C = {
    ink: v('--ink'), ink2: v('--ink-2'), muted: v('--muted'), grid: v('--grid'),
    axis: v('--axis'), surface: v('--surface'), series: v('--series'),
  };
}
function alpha(hex, a) {
  const n = parseInt(hex.slice(1), 16);
  return `rgba(${n >> 16 & 255},${n >> 8 & 255},${n & 255},${a})`;
}
const fmtTime = (ms, withSec) => new Date(ms).toLocaleString([], {
  month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', ...(withSec ? { second: '2-digit' } : {}),
});
const bucketName = s => s < 3600 ? `${s / 60}-min` : s < 86400 ? `${s / 3600}-hour` : `${s / 86400}-day`;

// Horizontal lines (pump start, backup start).
const refLines = {
  id: 'refLines',
  beforeDatasetsDraw(c) {
    const y = c.scales.y, { left, right, top, bottom } = c.chartArea, g = c.ctx;
    g.save();
    g.font = '12px system-ui, sans-serif';
    g.textAlign = 'right';
    g.textBaseline = 'bottom';
    for (const m of marks) {
      const py = Math.round(y.getPixelForValue(m.y)) + 0.5;
      if (py < top || py > bottom) continue;
      g.strokeStyle = C.muted; g.lineWidth = 1;
      g.beginPath(); g.moveTo(left, py); g.lineTo(right, py); g.stroke();
      g.fillStyle = C.ink2;
      g.fillText(`${m.label} · ${m.y} in`, right - 6, py - 4);
    }
    g.restore();
  },
};

// Vertical line under the tooltip.
const crosshair = {
  id: 'crosshair',
  afterDatasetsDraw(c) {
    const a = c.tooltip && c.tooltip.getActiveElements();
    if (!a || !a.length) return;
    const x = Math.round(a[0].element.x) + 0.5, { top, bottom } = c.chartArea, g = c.ctx;
    g.save(); g.strokeStyle = C.axis; g.lineWidth = 1;
    g.beginPath(); g.moveTo(x, top); g.lineTo(x, bottom); g.stroke(); g.restore();
  },
};

function onDblClick() {
  if (zoomed && dotnet) dotnet.invokeMethodAsync('OnZoomOut');
}

export function create(canvas, dotnetRef, markList) {
  dispose();
  dotnet = dotnetRef;
  marks = markList || [];
  readColors(canvas);

  chart = new Chart(canvas, {
    type: 'line',
    data: {
      datasets: [
        { label: 'Highest', data: [], fill: '+1', borderWidth: 0, pointRadius: 0, pointHoverRadius: 0,
          backgroundColor: alpha(C.series, 0.14) },
        { label: 'Lowest', data: [], fill: false, borderWidth: 0, pointRadius: 0, pointHoverRadius: 0,
          backgroundColor: alpha(C.series, 0.14) },
        { label: 'Level', data: [], borderWidth: 2, pointRadius: 0, pointHoverRadius: 5, pointHoverBorderWidth: 2,
          borderColor: C.series, backgroundColor: C.series, pointHoverBorderColor: C.surface,
          borderJoinStyle: 'round', borderCapStyle: 'round', tension: 0 },
      ],
    },
    options: {
      animation: false,
      maintainAspectRatio: false,
      parsing: false,
      normalized: true,
      spanGaps: false,
      color: C.ink2,
      interaction: { mode: 'index', intersect: false, axis: 'x' },
      scales: {
        x: {
          type: 'time',
          time: { displayFormats: { minute: 'h:mm a', hour: 'h a', day: 'MMM d' } },
          ticks: { maxRotation: 0, autoSkipPadding: 18, color: C.muted },
          grid: { display: false },
          border: { color: C.axis },
        },
        y: {
          beginAtZero: true,
          suggestedMax: 10,
          title: { display: true, text: 'inches', color: C.muted },
          ticks: { maxTicksLimit: 7, color: C.muted },
          grid: { color: C.grid },
          border: { color: C.axis },
        },
      },
      plugins: {
        legend: { display: false },
        tooltip: {
          filter: item => item.datasetIndex === 2 && item.parsed.y != null,
          displayColors: false,
          backgroundColor: C.surface, titleColor: C.ink, bodyColor: C.ink2,
          borderColor: C.axis, borderWidth: 1, padding: 10,
          callbacks: {
            title: items => items.length ? fmtTime(items[0].parsed.x, bucketSec === 0) : '',
            label: item => `${item.parsed.y.toFixed(1)} in${bucketSec ? ` (${bucketName(bucketSec)} average)` : ''}`,
            afterLabel: item => {
              if (!bucketSec) return '';
              const ds = chart.data.datasets;
              const lo = ds[1].data[item.dataIndex].y, hi = ds[0].data[item.dataIndex].y;
              return `Range ${lo.toFixed(1)}–${hi.toFixed(1)} in`;
            },
          },
        },
        zoom: {
          zoom: {
            mode: 'x',
            drag: { enabled: true, threshold: 10, backgroundColor: alpha(C.series, 0.12) },
            pinch: { enabled: true },
            wheel: { enabled: false },
            onZoomComplete: ({ chart: c }) => {
              if (ignoreZoom || !dotnet) return;
              zoomed = true;
              dotnet.invokeMethodAsync('OnZoom', c.scales.x.min, c.scales.x.max);
            },
          },
          limits: { x: { minRange: 2 * 60 * 1000 } },
        },
      },
    },
    plugins: [refLines, crosshair],
  });
  canvas.addEventListener('dblclick', onDblClick);
}

// points: [{ t, v, lo, hi }] from ReadingStore.ChartAsync
export function setData(start, end, bucketSeconds, points) {
  if (!chart) return;
  bucketSec = bucketSeconds;
  const gapMs = Math.max(60000, bucketSec * 3000);   // a longer hole means the ESP32 was offline: break the line
  const hi = [], lo = [], line = [];
  for (let i = 0; i < points.length; i++) {
    const p = points[i];
    if (i && p.t - points[i - 1].t > gapMs) {
      const x = (p.t + points[i - 1].t) / 2;
      hi.push({ x, y: null }); lo.push({ x, y: null }); line.push({ x, y: null });
    }
    hi.push({ x: p.t, y: p.hi }); lo.push({ x: p.t, y: p.lo }); line.push({ x: p.t, y: p.v });
  }
  const ds = chart.data.datasets;
  ds[0].data = hi; ds[1].data = lo; ds[2].data = line;
  ds[0].hidden = ds[1].hidden = bucketSec === 0;
  const x = chart.options.scales.x;
  x.min = start;
  x.max = end;
  x.time.unit = end - start > 2.5 * 86400e3 ? 'day' : false;   // label days, not hours, on long spans
  chart.update('none');
}

export function resetZoom() {
  zoomed = false;
  if (!chart) return;
  ignoreZoom = true;
  try { chart.resetZoom('none'); } finally { ignoreZoom = false; }
}

export function loadHours() {
  try { const v = Number(localStorage.getItem('sump.hours')); return v > 0 ? v : null; } catch { return null; }
}
export function saveHours(h) {
  try { localStorage.setItem('sump.hours', String(h)); } catch { }
}

export function dispose() {
  if (chart) {
    chart.canvas.removeEventListener('dblclick', onDblClick);
    chart.destroy();
  }
  chart = null;
  dotnet = null;
  zoomed = false;
}
