import test from 'node:test';
import assert from 'node:assert/strict';
import { maxFilter, normalizeRect, prepareInk, removeSpeckles, renderRegion, trimAndPad, detectRegions } from '../lib/processing.js';

function image(w=220,h=130) {
  const data=new Uint8ClampedArray(w*h*4);data.fill(255);return {width:w,height:h,data};
}
function dot(im,x,y,color=[20,50,170],alpha=255) { im.data.set([...color,alpha],(y*im.width+x)*4); }
function line(im,x1,y1,x2,y2,color=[20,50,170]) {
  const n=Math.max(Math.abs(x2-x1),Math.abs(y2-y1));
  for(let i=0;i<=n;i++){ const x=Math.round(x1+(x2-x1)*i/n),y=Math.round(y1+(y2-y1)*i/n);dot(im,x,y,color);dot(im,x,Math.min(y+1,im.height-1),color); }
}
function sample() {
  const im=image(360,180);
  // Two unrelated rings and two separate, deliberately synthetic zigzags.
  for(const [cx,cy,color] of [[48,45,[20,50,170]],[270,47,[170,20,30]]]) {
    for(let a=0;a<Math.PI*2;a+=0.008)dot(im,Math.round(cx+28*Math.cos(a)),Math.round(cy+28*Math.sin(a)),color);
  }
  for(const [x,y,color] of [[20,130,[10,15,20]],[220,125,[20,50,170]]]) {
    line(im,x,y,x+20,y-17,color);line(im,x+20,y-17,x+35,y+8,color);line(im,x+35,y+8,x+70,y-7,color);
  }
  return im;
}

test('local maximum matches a naive rectangular maximum at edges',()=>{
  const w=7,h=5,input=Uint8Array.from({length:w*h},(_,i)=>(i*19)%31);
  const output=maxFilter(input,w,h,2,1);
  for(let y=0;y<h;y++)for(let x=0;x<w;x++){
    let m=0;for(let dy=-1;dy<=1;dy++)for(let dx=-2;dx<=2;dx++)if(x+dx>=0&&x+dx<w&&y+dy>=0&&y+dy<h)m=Math.max(m,input[(y+dy)*w+x+dx]);
    assert.equal(output[y*w+x],m);
  }
});
test('rectangle clipping preserves the true intersection and rejects empty areas',()=>{
  assert.deepEqual(normalizeRect({x:-10,y:-5,w:30,h:20},100,100),{x:0,y:0,w:20,h:15});
  assert.throws(()=>normalizeRect({x:500,y:0,w:10,h:10},100,100));
  assert.throws(()=>normalizeRect({x:NaN,y:0,w:10,h:10},100,100));
});
test('blank and transparent pages produce no foreground or candidates',()=>{
  const im=image();for(let p=0;p<im.data.length;p+=4){im.data[p]=0;im.data[p+1]=0;im.data[p+2]=0;im.data[p+3]=0;}
  assert.equal(prepareInk(im).mask.some(Boolean),false);
  assert.equal(detectRegions(im).regions.length,0);
});
test('smooth photographic shadow is removed while ink survives',()=>{
  const im=image(220,130);for(let y=0;y<130;y++)for(let x=0;x<220;x++)dot(im,x,y,[0,0,0].map(()=>Math.round(180+x/220*60)));
  line(im,50,65,160,74,[25,25,25]);
  const result=renderRegion(im,{x:0,y:0,w:220,h:130},{threshold:30});
  assert.deepEqual([...result.data.subarray(0,4)],[255,255,255,255]);
  assert.ok(result.inkPixels>100&&result.inkPixels<400);
});
test('bright compression outliers do not turn gray paper into foreground',()=>{
  const im=image(220,130);
  for(let y=0;y<130;y++)for(let x=0;x<220;x++)dot(im,x,y,[225,225,225]);
  dot(im,110,60,[255,255,255]);line(im,30,85,100,90,[20,20,20]);
  dot(im,8,55,[255,255,255]);
  const cleaned=renderRegion(im,{x:0,y:0,w:220,h:130});
  assert.ok(cleaned.inkPixels>100&&cleaned.inkPixels<400);
  assert.deepEqual([...cleaned.data.subarray((60*220+90)*4,(60*220+90)*4+4)],[255,255,255,255]);
  assert.deepEqual([...cleaned.data.subarray((55*220)*4,(55*220)*4+4)],[255,255,255,255]);
});
test('blue/red/black filters preserve the requested strokes',()=>{
  const im=image();line(im,20,30,80,35);line(im,20,65,80,70,[180,25,35]);line(im,20,95,80,100,[20,20,20]);
  const blue=prepareInk(im,{mode:'blue'}).mask,red=prepareInk(im,{mode:'red'}).mask;
  assert.equal(blue[30*220+20],1);assert.equal(blue[65*220+20],0);assert.equal(blue[95*220+20],0);
  assert.equal(red[65*220+20],1);assert.equal(red[30*220+20],0);
});
test('small noise is removed without losing a continuous narrow stroke',()=>{
  const mask=new Uint8Array(100);mask[0]=1;for(let x=2;x<9;x++)mask[50+x]=1;
  removeSpeckles(mask,10,10,3);assert.equal(mask[0],0);assert.equal(mask[55],1);
});
test('four separated synthetic objects become four independent crops',()=>{
  const detected=detectRegions(sample(),{gap:7});assert.equal(detected.regions.length,4);
  for(const region of detected.regions){const cleaned=renderRegion(sample(),region);assert.ok(cleaned.inkPixels>50);assert.ok(trimAndPad(cleaned).width>10);}
});
test('disconnected interior marks stay inside the enclosing stamp crop',()=>{
  const im=sample();line(im,42,45,54,45);line(im,48,39,48,51);
  assert.equal(detectRegions(im,{gap:4}).regions.length,4);
});
test('manual erase/restore and undo restore actual source pixels',()=>{
  const im=image(100,60);line(im,10,30,90,30,[20,20,20]);const rect={x:0,y:0,w:100,h:60};
  const erase={tool:'erase',radius:8,points:[{x:45,y:30},{x:55,y:30}]};
  const restore={...erase,tool:'restore'};
  const base=renderRegion(im,rect),removed=renderRegion(im,rect,{},[erase]),recovered=renderRegion(im,rect,{},[erase,restore]);
  assert.ok(removed.inkPixels<base.inkPixels);assert.deepEqual(recovered.data,base.data);
  assert.deepEqual(renderRegion(im,rect,{},[]).data,base.data);
});
test('restoring white background does not create new strokes',()=>{
  const im=image(60,40);const restored=renderRegion(im,{x:0,y:0,w:60,h:40},{},[{tool:'restore',radius:20,points:[{x:25,y:20}]}]);
  assert.equal(restored.inkPixels,0);assert.ok(restored.data.every(v=>v===255));
});
test('export trims blank margins and uses opaque pure white padding',()=>{
  const im=image(90,60);line(im,20,20,60,25);
  const clean=renderRegion(im,{x:0,y:0,w:90,h:60});const output=trimAndPad(clean,12);
  assert.equal(output.width,65);assert.equal(output.height,31);
  for(let x=0;x<output.width;x++)assert.deepEqual([...output.data.subarray(x*4,x*4+4)],[255,255,255,255]);
  for(let p=3;p<output.data.length;p+=4)assert.equal(output.data[p],255);
});
test('empty export reports a useful error instead of saving a false result',()=>{
  assert.throws(()=>trimAndPad(image()),/область пуста/);
});
