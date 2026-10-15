import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { PNG } from 'pngjs';
import { analyzeDocument, renderExtractedRegion, scaleAnalysis } from '../lib/extraction.js';
import { cropImage, prepareInk } from '../lib/processing.js';
import { pointInStampFrame } from '../lib/stamps.js';

const loadPng=path=>PNG.sync.read(readFileSync(new URL(path,import.meta.url)));
const dark=(image,index)=>Math.min(image.data[index*4],image.data[index*4+1],image.data[index*4+2])<245;
let authored;
function challenge() {
  if(authored)return authored;
  const image=loadPng('../examples/failures-rectangular.png');
  const truth=JSON.parse(readFileSync(new URL('./fixtures-v3/failures-rectangular.json',import.meta.url)));
  // Test annotations never enter production analysis. Only the ordinary OCR
  // contract is supplied, just as returned by the offline OCR worker.
  const words=truth.printed_words.map(({text,confidence,bbox,symbols})=>({text,confidence,bbox,symbols}));
  const analysis=analyzeDocument(image,words);
  const region=analysis.regions.filter(r=>r.kind==='stamp').sort((a,b)=>a.y-b.y)[1];
  assert.ok(region,'lower approval stamp found');
  const frame=analysis.protectedStamps.find(s=>s.quad&&s.y>300);
  assert.ok(frame);
  authored={image,truth,words,analysis,region,frame};return authored;
}

test('renderer whole-stamp mode retains measured handwriting and printed mode removes it',()=>{
  const s=challenge(),common={kind:'stamp',mode:'blue',repair:false,removeLines:false,isolate:true,threshold:18,speckle:3,contrast:1.2};
  const whole=renderExtractedRegion(s.image,s.region,{...common,stampLayer:'whole'},[],s.analysis);
  const printed=renderExtractedRegion(s.image,s.region,{...common,stampLayer:'printed'},[],s.analysis);
  const reference=prepareInk(cropImage(s.image,s.region),common);
  const background=loadPng('./fixtures-v3/'+s.truth.backgrounds.find(b=>b.id==='handwritten-body').mask);
  const letters=loadPng('./fixtures-v3/'+s.truth.objects.find(o=>o.id==='approval-bottom').letter_mask);
  let sourcePixels=0,wholePixels=0,penPixels=0,wholePen=0,printedPen=0;
  for(let y=0;y<whole.height;y++)for(let x=0;x<whole.width;x++) {
    const i=y*whole.width+x,gx=x+s.region.x,gy=y+s.region.y,global=gy*s.image.width+gx;
    if(!pointInStampFrame(s.frame,gx,gy,2.5)) {
      assert.equal(dark(whole,i),false,'whole mode still clips to the tilted frame');continue;
    }
    if(!reference.mask[i]||!dark(reference,i))continue;
    sourcePixels++;if(dark(whole,i))wholePixels++;
    if(background.data[global*4]>128&&letters.data[global*4]<128) {
      penPixels++;if(dark(whole,i))wholePen++;if(dark(printed,i))printedPen++;
    }
  }
  assert.ok(sourcePixels>10000);assert.equal(wholePixels,sourcePixels,'whole mode must not run foreign-colour suppression');
  assert.ok(penPixels>1000);assert.ok(wholePen/penPixels>.99);
  assert.ok(printedPen/penPixels<.03,'printed mode suppresses handwritten background');
});

test('scaled analysis keeps frame, OCR symbols and selection coordinate systems consistent',()=>{
  const s=challenge(),scaled=scaleAnalysis(s.analysis,2,1.5);
  const sourceWord=s.analysis.ocrWords.find(w=>w.symbols?.length),word=scaled.ocrWords[s.analysis.ocrWords.indexOf(sourceWord)];
  assert.equal(word.bbox.x0,sourceWord.bbox.x0*2);assert.equal(word.bbox.y1,sourceWord.bbox.y1*1.5);
  assert.equal(word.symbols[0].bbox.x1,sourceWord.symbols[0].bbox.x1*2);
  assert.equal(word.symbols[0].bbox.y0,sourceWord.symbols[0].bbox.y0*1.5);
  const normalized=s.analysis.textBoxes.find(w=>w.symbols?.length),normalizedScaled=scaled.textBoxes[s.analysis.textBoxes.indexOf(normalized)];
  assert.equal(normalizedScaled.symbols[0].bbox.x0,normalized.symbols[0].bbox.x0*2);
  assert.equal(normalizedScaled.symbols[0].bbox.y1,normalized.symbols[0].bbox.y1*1.5);
  const originalFrame=s.analysis.protectedStamps.find(f=>f.quad),frame=scaled.protectedStamps[s.analysis.protectedStamps.indexOf(originalFrame)];
  for(let n=0;n<4;n++)assert.deepEqual(frame.quad[n],{x:originalFrame.quad[n].x*2,y:originalFrame.quad[n].y*1.5});
  const region=s.analysis.regions.find(r=>r.selection);
  assert.ok(region,'analysis produced a signature selection');
  const mapped=scaled.regions[s.analysis.regions.indexOf(region)];
  assert.equal(mapped.selection.x,region.selection.x*2);assert.equal(mapped.selection.h,region.selection.h*1.5);
  assert.equal(mapped.selection.width,region.selection.width,'mask grid stays at analysis resolution');
  assert.equal(mapped.selection.height,region.selection.height);
  assert.equal(mapped.selection.mask,region.selection.mask);
  assert.notEqual(frame.quad,originalFrame.quad,'scaling does not mutate original frame');
});

test('stamp rendering shifts OCR symbols and tilted frame into the selected crop',()=>{
  const s=challenge(),rect={x:s.region.x-15,y:s.region.y-13,w:s.region.w+30,h:s.region.h+26};
  const options={kind:'stamp',mode:'blue',stampLayer:'printed',repair:false};
  const tight=renderExtractedRegion(s.image,s.region,options,[],s.analysis),wide=renderExtractedRegion(s.image,rect,options,[],s.analysis);
  let compared=0,matches=0;
  for(let y=0;y<tight.height;y++)for(let x=0;x<tight.width;x++) {
    const i=y*tight.width+x,j=(y+13)*wide.width+x+15;
    if(!dark(tight,i)&&!dark(wide,j))continue;
    compared++;if(dark(tight,i)===dark(wide,j))matches++;
  }
  assert.ok(compared>10000);assert.ok(matches/compared>.98,'changing crop margins must not shift lettering masks');
});

function crossing() {
  const width=130,height=100,data=new Uint8ClampedArray(width*height*4);data.fill(255);
  for(let n=0;n<=80;n++) {
    const x=25+n,y=Math.round(12+n*.9);
    for(let dy=0;dy<2;dy++)data.set([25,20,130,255],((y+dy)*width+x)*4);
  }
  for(let y=46;y<=49;y++)for(let x=4;x<125;x++)data.set([15,15,15,255],(y*width+x)*4);
  return {image:{width,height,data},rect:{x:10,y:5,w:110,h:90},analysis:{regions:[],protectedStamps:[],textBoxes:[],ocrWords:[],ruleBoxes:[{x:4,y:46,w:121,h:4}]}};
}

test('manual eraser overrides inferred repair in exported pixels and in preview highlight mask',()=>{
  const s=crossing(),options={kind:'signature',mode:'blue',repair:true,repairGap:12,removeText:false,isolate:false,threshold:12,speckle:0};
  const repaired=renderExtractedRegion(s.image,s.rect,options,[],s.analysis);
  assert.ok(repaired.inferredPixels>0,'short source-rule crossing is repaired');
  const index=repaired.inferredMask.findIndex(Boolean),x=index%repaired.width,y=Math.floor(index/repaired.width);
  assert.equal(dark(repaired,index),true);
  const stroke={tool:'erase',radius:3,points:[{x,y}]};
  const erased=renderExtractedRegion(s.image,s.rect,options,[stroke],s.analysis);
  assert.equal(dark(erased,index),false,'eraser wins over automatic repair');
  assert.equal(erased.inferredMask[index],0,'preview must not highlight an already erased inferred pixel');
  assert.equal(erased.inferredPixels,erased.inferredMask.reduce((sum,pixel)=>sum+!!pixel,0));
  const restored=renderExtractedRegion(s.image,s.rect,options,[stroke,{...stroke,tool:'restore'}],s.analysis);
  assert.equal(dark(restored,index),false,'restore means original ink, not invention at an occluded position');
  const disabled=renderExtractedRegion(s.image,s.rect,{...options,repair:false},[],s.analysis);
  assert.equal(disabled.inferredPixels,0);assert.equal(dark(disabled,index),false);
});
