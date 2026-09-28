let timer, anchor, index=0, pose=45, wallTime;
onmessage = ({data}) => {
  if(data.type==='pose') pose=data.value;
  if(data.type==='start') {
    clearInterval(timer);anchor=performance.now();wallTime=Date.now();index=0;
    timer=setInterval(()=>{
      const target=Math.floor((performance.now()-anchor)/10), batch=[];
      // Synthetic time grid, not a guarantee of real hardware timing.
      while(index<target){batch.push({sampleIndex:index,elapsedMs:index*10,timestamp:wallTime+index*10,ch1Voltage:2.5+(pose-45)/45+Math.sin(index*.13)*.002,ch2Voltage:2});index++;}
      if(batch.length)postMessage(batch);
    },20);
  }
  if(data.type==='stop')clearInterval(timer);
};
