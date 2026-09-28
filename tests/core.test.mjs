import {test} from 'node:test';
import assert from 'node:assert/strict';
import {calculateAngle,calculateCalibration,validateCalibration,movingAverage,generateCsv} from '../packages/measurement-core/index.mjs';
test('2点校正、内挿と外挿、逆向きセンサ',()=>{
  assert.equal(calculateAngle(0.5,0.5,1.5),45);assert.equal(calculateAngle(1.5,0.5,1.5),90);
  assert.equal(calculateAngle(1,0.5,1.5),67.5);assert.equal(calculateAngle(2,0.5,1.5),112.5);
  assert.equal(calculateAngle(0,0.5,1.5),22.5);assert.equal(calculateAngle(1,1.5,0.5),67.5);
});
test('校正不成立と非数値を拒否',()=>{for(const [a,b] of [[1,1],[1,1.001],[NaN,1],[1,Infinity]]){assert.equal(validateCalibration(a,b),false);assert.throws(()=>calculateAngle(1,a,b));}});
test('実測平均で校正値を更新',()=>{assert.deepEqual(calculateCalibration([{ch1Voltage:4,ch2Voltage:2},{ch1Voltage:6,ch2Voltage:3}]),{ch1Voltage:5,ch2Voltage:2.5,diffVoltage:2.5,sampleCount:2});assert.throws(()=>calculateCalibration([]));});
test('移動平均は10点、開始時は取得済み点数',()=>{assert.deepEqual(movingAverage([1,2,3,4],3),[1,1.5,2,3]);assert.equal(movingAverage(Array.from({length:11},(_,i)=>i)).at(-1),5.5);});
test('CSV列とエスケープ',()=>{const csv=generateCsv([{timestamp_iso:'a"b',elapsed_ms:0,paused:false}]);assert.match(csv,/diff_voltage/);assert.match(csv,/"a""b","0"/);assert.match(csv,/"false"/);});
