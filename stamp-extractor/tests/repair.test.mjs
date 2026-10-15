import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {PNG} from 'pngjs';
import {repairStrokes} from '../lib/repair.js';
import {prepareInk} from '../lib/processing.js';

function scene(width=100,height=70) {
  const mask=new Uint8Array(width*height),removedMask=new Uint8Array(width*height),data=new Uint8ClampedArray(width*height*4);data.fill(255);
  return {width,height,mask,removedMask,data};
}
function stroke(image,x0,y0,x1,y1,color=[25,35,150],thickness=1) {
  const n=Math.max(Math.abs(x1-x0),Math.abs(y1-y0));
  for(let step=0;step<=n;step++) {
    const x=Math.round(x0+(x1-x0)*step/n),y=Math.round(y0+(y1-y0)*step/n);
    for(let dy=0;dy<thickness;dy++) {
      image.mask[(y+dy)*image.width+x]=1;
      image.data.set([...color,255],((y+dy)*image.width+x)*4);
    }
  }
}
function removeRows(image,top,bottom) {
  for(let y=top;y<=bottom;y++)for(let x=0;x<image.width;x++) {
    const i=y*image.width+x;image.removedMask[i]=1;image.mask[i]=0;
  }
}

test('bridges a short blue pen stroke interrupted by a known removed horizontal rule',()=>{
  const im=scene();stroke(im,18,10,72,58);removeRows(im,31,34);
  const originalMask=new Uint8Array(im.mask),originalData=new Uint8ClampedArray(im.data);
  const result=repairStrokes(im);
  assert.equal(result.bridges,1);assert.ok(result.inferredPixels>=4&&result.inferredPixels<18);
  assert.equal(result.mask[33*im.width+44],1);
  assert.deepEqual(im.mask,originalMask);assert.deepEqual(im.data,originalData);
  for(let i=0;i<im.mask.length;i++)if(result.inferredMask[i]) {
    assert.equal(im.mask[i],0);assert.deepEqual([...result.data.subarray(i*4,i*4+4)],[25,35,150,255]);
  }
});

test('uses rule evidence to connect colored curves occluded by black source pixels',()=>{
  const im=scene(130,100);stroke(im,25,12,105,84,[25,20,130],2);
  const ruleMask=new Uint8Array(im.mask.length);
  for(let y=46;y<=49;y++)for(let x=4;x<125;x++) {
    const i=y*im.width+x;im.data.set([15,15,15,255],i*4);ruleMask[i]=1;
  }
  const prepared=prepareInk(im,{mode:'blue',speckle:0,threshold:12});
  const result=repairStrokes({...prepared,ruleMask});
  assert.equal(result.bridges,1);assert.ok(result.inferredPixels>3);
  for(let i=0;i<result.inferredMask.length;i++)if(result.inferredMask[i])assert.ok(result.data[i*4+2]>result.data[i*4]+70);
});

test('enabled false leaves original strokes and colors untouched',()=>{
  const im=scene();stroke(im,25,5,65,60);removeRows(im,28,32);
  const result=repairStrokes(im,{enabled:false});
  assert.equal(result.bridges,0);assert.equal(result.inferredPixels,0);assert.deepEqual(result.mask,im.mask);assert.deepEqual(result.data,im.data);
});

test('does not connect separate aligned strokes without evidence of removal',()=>{
  const im=scene();stroke(im,20,10,40,30);stroke(im,45,35,70,60);
  const result=repairStrokes(im);
  assert.equal(result.inferredPixels,0);assert.deepEqual(result.mask,im.mask);
});

test('does not infer a large missing section',()=>{
  const im=scene();stroke(im,20,5,70,63);removeRows(im,21,43);
  const result=repairStrokes(im,{maxGap:12});
  assert.equal(result.bridges,0);assert.deepEqual(result.mask,im.mask);
});

test('does not join nearby parallel strokes sideways',()=>{
  const im=scene();stroke(im,20,20,65,20);stroke(im,20,25,65,25);
  im.removedMask.fill(1);
  const result=repairStrokes(im);
  assert.equal(result.bridges,0);assert.deepEqual(result.mask,im.mask);
});

test('does not join differently oriented stroke ends across a deleted label',()=>{
  const im=scene();stroke(im,10,15,38,35);stroke(im,44,35,78,15);
  for(let y=28;y<=40;y++)for(let x=39;x<=43;x++)im.removedMask[y*im.width+x]=1;
  const result=repairStrokes(im);
  assert.equal(result.bridges,0);assert.deepEqual(result.mask,im.mask);
});

test('preserves an intact rectangular seal, its letters, and enclosed strokes',()=>{
  const im=scene();stroke(im,10,10,85,10);stroke(im,85,10,85,60);stroke(im,85,60,10,60);stroke(im,10,60,10,10);
  stroke(im,25,25,25,45);stroke(im,25,25,38,25);stroke(im,25,35,36,35);stroke(im,52,25,52,45);stroke(im,52,45,67,45);
  im.removedMask.fill(1);
  const result=repairStrokes(im);
  assert.equal(result.bridges,0);assert.deepEqual(result.mask,im.mask);assert.deepEqual(result.data,im.data);
});

test('sourceMask gates arbitrary removal regions but explicit form rules remain usable',()=>{
  const im=scene();stroke(im,18,10,72,58);removeRows(im,31,34);
  const result=repairStrokes({...im,sourceMask:new Uint8Array(im.mask.length)});
  assert.equal(result.bridges,0);
  const ruled=repairStrokes({...im,sourceMask:new Uint8Array(im.mask.length),ruleMask:im.removedMask});
  assert.equal(ruled.bridges,1);
});

test('rejects malformed dimensions and inconsistent masks',()=>{
  assert.throws(()=>repairStrokes({width:5,height:4,mask:new Uint8Array(3)}),/маска/);
  assert.throws(()=>repairStrokes({...scene(),removedMask:new Uint8Array(2)}),/Размер/);
  assert.throws(()=>repairStrokes({...scene(),data:new Uint8ClampedArray(2)}),/Размер/);
});


test('authored long-loop document repairs measured rule gaps without broad stroke invention',()=>{
  const directory=new URL('./fixtures-v3/',import.meta.url);
  const spec=JSON.parse(fs.readFileSync(new URL('failures-form-signature.json',directory),'utf8'));
  function readMask(name) {
    const png=PNG.sync.read(fs.readFileSync(new URL(name,directory)));
    return Uint8Array.from({length:png.width*png.height},(_,i)=>png.data[i*4]>32?1:0);
  }
  const object=spec.objects[0],mask=readMask(object.visible_mask),reference=readMask(object.mask);
  const ruleMask=readMask(spec.backgrounds.find(b=>b.id==='form-rules').mask);
  const data=PNG.sync.read(fs.readFileSync(new URL('../examples/failures-form-signature.png',import.meta.url))).data;
  const result=repairStrokes({width:spec.width,height:spec.height,mask,ruleMask,data});
  let onReference=0;
  for(let i=0;i<mask.length;i++) {
    if(mask[i])assert.equal(result.mask[i],1,'source stroke removed during repair');
    if(result.inferredMask[i]&&reference[i])onReference++;
  }
  assert.ok(result.bridges>=2,'known short curve crossings should reconnect');
  assert.ok(result.inferredPixels<100,'inference should stay local to short cuts');
  assert.ok(onReference/result.inferredPixels>.9,'most inferred pixels should lie on authored hidden curve');
});
