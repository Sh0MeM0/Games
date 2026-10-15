import test from 'node:test';
import assert from 'node:assert/strict';
import { analyzeDocument, renderExtractedRegion, scaleAnalysis } from '../lib/extraction.js';

function canvas(width=640,height=480) { const data=new Uint8ClampedArray(width*height*4);data.fill(255);return {width,height,data}; }
function dot(image,x,y,color,radius=1) {
  for(let yy=Math.floor(y-radius);yy<=Math.ceil(y+radius);yy++)for(let xx=Math.floor(x-radius);xx<=Math.ceil(x+radius);xx++) {
    if(xx<0||yy<0||xx>=image.width||yy>=image.height||(xx-x)**2+(yy-y)**2>radius**2)continue;
    const p=(yy*image.width+xx)*4;image.data.set([...color,255],p);
  }
}
function line(image,x0,y0,x1,y1,color,radius=1) { const steps=Math.ceil(Math.hypot(x1-x0,y1-y0)*2);for(let i=0;i<=steps;i++)dot(image,x0+(x1-x0)*i/steps,y0+(y1-y0)*i/steps,color,radius); }
function ring(image,cx,cy,radius,color) { for(let angle=0;angle<Math.PI*2;angle+=.004)dot(image,cx+Math.cos(angle)*radius,cy+Math.sin(angle)*radius,color,1.8); }
function script(image,x,y,color) {
  const points=[[0,40],[20,5],[30,62],[48,15],[60,48],[83,0],[95,45],[125,13],[139,37],[175,23],[193,39],[230,19]];
  for(let i=1;i<points.length;i++)line(image,x+points[i-1][0],y+points[i-1][1],x+points[i][0],y+points[i][1],color,1.8);
}
function print(image,x,y,color=[25,25,25],letters=8) {
  for(let i=0;i<letters;i++) {
    const xx=x+i*16;line(image,xx,y,xx,y+20,color,1.2);line(image,xx,y,xx+9,y,color,1.2);line(image,xx,y+10,xx+8,y+10,color,1.2);
  }
  return {text:'PRINTED'.repeat(Math.ceil(letters/7)).slice(0,letters),confidence:98,bbox:{x0:x-2,y0:y-2,x1:x+letters*16,y1:y+23}};
}
function dark(image,x,y) { const p=(y*image.width+x)*4;return Math.min(image.data[p],image.data[p+1],image.data[p+2])<230; }
const blue=[25,65,155],red=[170,35,40],black=[25,25,25];

test('OCR printed paragraphs and form lines produce no signature files',()=>{
  const image=canvas(),words=[];
  for(let y=30;y<350;y+=40)words.push(print(image,30,y,black,30));
  line(image,20,420,610,420,black,1);
  const result=analyzeDocument(image,words);
  assert.equal(result.regions.length,0);
  assert.ok(result.stats.removedTextPixels>10000);
  assert.ok(result.ruleBoxes.length>0);
});

test('colored printed text is suppressed too, not mistaken for a signature',()=>{
  const image=canvas(),words=[print(image,40,60,blue,20),print(image,40,120,red,20)];
  assert.equal(analyzeDocument(image,words).regions.length,0);
});

test('two seal colors and two handwriting strokes become four separate objects',()=>{
  const image=canvas(900,600),words=[print(image,30,25,black,30)];
  ring(image,180,200,70,blue);ring(image,650,200,70,red);
  words.push(print(image,140,190,blue,5),print(image,610,190,red,5));
  script(image,55,400,blue);script(image,525,400,black);
  const result=analyzeDocument(image,words);
  assert.equal(result.regions.length,4);
  assert.deepEqual(result.regions.map(r=>r.kind),['stamp','stamp','signature','signature']);
  assert.deepEqual(result.regions.map(r=>r.mode),['blue','red','blue','black']);
  assert.equal(analyzeDocument(image,words,{mode:'blue'}).regions.length,2);
  assert.equal(analyzeDocument(image,words,{mode:'colored'}).regions.length,3);
});

test('seal own OCR lettering is retained while black printed ink is removed',()=>{
  const image=canvas(),words=[];
  ring(image,220,220,90,blue);
  words.push(print(image,170,205,blue,5));
  // Black text inside the circular seal, separate from its own blue lettering.
  words.push(print(image,175,245,black,5));
  const a=analyzeDocument(image,words),r=a.regions.find(r=>r.kind==='stamp');
  assert.ok(r);
  const out=renderExtractedRegion(image,r,r,[],a);
  assert.equal(dark(out,170-r.x,212-r.y),true,'blue seal lettering retained');
  assert.equal(dark(out,175-r.x,252-r.y),false,'black overprinted text removed');
});

test('high-confidence OCR of cursive handwriting cannot erase connected signature strokes',()=>{
  const image=canvas();script(image,140,200,black);
  const words=[{text:'Иванов',confidence:99,bbox:{x0:138,y0:198,x1:375,y1:267}}];
  const a=analyzeDocument(image,words);
  assert.equal(a.regions.length,1);
  assert.equal(a.regions[0].kind,'signature');
  const r=a.regions[0],out=renderExtractedRegion(image,r,r,[],a);
  assert.ok(out.inkPixels>1200);
  assert.equal(dark(out,160-r.x,205-r.y),true);
});

test('manual signature crop removes ordinary surrounding text but keeps original ink',()=>{
  const image=canvas(),words=[print(image,120,95,black,15)];script(image,140,200,black);
  const a=analyzeDocument(image,words),rect={x:90,y:70,w:330,h:240};
  const out=renderExtractedRegion(image,rect,{kind:'signature',mode:'black'},[],a);
  assert.equal(dark(out,120-rect.x,105-rect.y),false);
  assert.equal(dark(out,160-rect.x,205-rect.y),true);
  assert.ok(out.removedTextPixels>1000);
  assert.equal(image.data[(105*image.width+120)*4],25,'original source stays untouched');
});

test('analysis coordinate scaling preserves OCR boxes and stamp geometry',()=>{
  const image=canvas();ring(image,220,220,90,blue);const a=analyzeDocument(image,[]),b=scaleAnalysis(a,2,3);
  assert.equal(b.width,1280);assert.equal(b.height,1440);
  assert.equal(b.regions[0].x,a.regions[0].x*2);
  assert.equal(b.protectedStamps[0].h,a.protectedStamps[0].h*3);
  assert.equal(a.width,640);
});

test('slanted form rules are rejected and only their axis is erased',()=>{
  const image=canvas(900,600);
  line(image,50,400,800,435,black,1.2);
  script(image,250,300,black);
  // A small dot lies within the slanted rule's bounding box but away from its axis.
  dot(image,100,425,black,2);
  const a=analyzeDocument(image,[]);
  assert.equal(a.regions.length,1);
  assert.equal(a.regions[0].kind,'signature');
  assert.ok(a.ruleBoxes.some(r=>r.line));
  const out=renderExtractedRegion(image,{x:0,y:0,w:900,h:600},{mode:'black'},[],a);
  assert.equal(dark(out,400,416),false,'straight slanted form rule erased');
  assert.equal(dark(out,100,425),true,'unrelated original ink inside rule bbox retained');
  const scaled=scaleAnalysis(a,2);
  assert.equal(scaled.ruleBoxes.find(r=>r.line).line.cx,a.ruleBoxes.find(r=>r.line).line.cx*2);
});

import { readFileSync } from 'node:fs';
import { PNG } from 'pngjs';
function readPng(relative) { return PNG.sync.read(readFileSync(new URL(relative,import.meta.url))); }
function halfImage(source) {
  const image=canvas(source.width/2,source.height/2);
  for(let y=0;y<image.height;y++)for(let x=0;x<image.width;x++)for(let c=0;c<4;c++)image.data[(y*image.width+x)*4+c]=source.data[(y*2*source.width+x*2)*4+c];
  return image;
}
function overlap(a,b) { return Math.max(0,Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x))*Math.max(0,Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y)); }

test('authored document: exact four objects, preserved ink, grayscale and half-size invariance',()=>{
  const truth=JSON.parse(readFileSync(new URL('./fixtures/document.json',import.meta.url))),source=readPng('../examples/document.png');
  for(const variant of [source,readPng('../examples/document-gray.png'),halfImage(source)]) {
    const scale=variant.width/source.width;
    const words=truth.printed_words.map(w=>({...w,bbox:Object.fromEntries(Object.entries(w.bbox).map(([key,value])=>[key,value*scale]))}));
    const a=analyzeDocument(variant,words);
    assert.equal(a.regions.length,4,`four objects at ${variant.width}px`);
    assert.equal(a.regions.filter(r=>r.kind==='stamp').length,2);
    for(const expected of truth.objects) {
      const b=expected.bbox,box={x:b.x*scale,y:b.y*scale,w:b.width*scale,h:b.height*scale};
      const region=a.regions.find(r=>r.kind===expected.kind&&overlap(r,box)>box.w*box.h*.95);
      assert.ok(region,`located ${expected.id}`);
      if(scale!==1)continue;
      const out=renderExtractedRegion(variant,region,region,[],a),mask=readPng('./fixtures/'+expected.mask);
      let total=0,retained=0;
      for(let y=0;y<out.height;y++)for(let x=0;x<out.width;x++) {
        if(mask.data[((y+region.y)*mask.width+x+region.x)*4]<128)continue;
        total++;if(dark(out,x,y))retained++;
      }
      assert.ok(retained/total>.95,`${expected.id}: ${Math.round(retained/total*100)}% original ink retained`);
    }
  }
  for(const filename of ['document-text-only.png','document-text-only-blue.png']) {
    const result=analyzeDocument(readPng('./fixtures/'+filename),truth.printed_words);
    assert.equal(result.regions.length,0,filename+' creates no bogus signature files');
  }
});
