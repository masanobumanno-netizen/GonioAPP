export function validateCalibration(a, b, threshold = 0.01) {
  return Number.isFinite(a) && Number.isFinite(b) && Number.isFinite(threshold) && threshold > 0 && Math.abs(b - a) >= threshold;
}
export function calculateAngle(diff, d45, d90, threshold = 0.01) {
  if (!Number.isFinite(diff) || !validateCalibration(d45, d90, threshold)) throw new Error('校正差分が不足しています。再校正してください。');
  return 45 + (diff - d45) / (d90 - d45) * 45;
}
export function calculateCalibration(samples) {
  if (!samples.length || samples.some(s => !Number.isFinite(s.ch1Voltage) || !Number.isFinite(s.ch2Voltage))) throw new Error('有効な校正データがありません。');
  const ch1 = samples.reduce((n,s) => n+s.ch1Voltage,0)/samples.length;
  const ch2 = samples.reduce((n,s) => n+s.ch2Voltage,0)/samples.length;
  return {ch1Voltage:ch1, ch2Voltage:ch2, diffVoltage:ch1-ch2, sampleCount:samples.length};
}
export function movingAverage(values, window = 10) {
  if (!Number.isInteger(window) || window < 1 || values.some(v => !Number.isFinite(v))) throw new Error('移動平均の入力が不正です。');
  let sum = 0;
  return values.map((v,i) => {sum += v; if(i >= window) sum -= values[i-window]; return sum / Math.min(i+1,window);});
}
export const CSV_COLUMNS = ['timestamp_iso','elapsed_ms','sample_index','device_sample_index','ch1_voltage','ch2_voltage','diff_voltage','raw_angle_deg','smoothed_angle_deg','paused'];
export function generateCsv(samples) {
  const cell = v => '"'+String(v ?? '').replaceAll('"','""')+'"';
  return '\uFEFF'+CSV_COLUMNS.join(',')+'\r\n'+samples.map(s => CSV_COLUMNS.map(k=>cell(s[k])).join(',')).join('\r\n');
}
