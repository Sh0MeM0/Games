import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {PNG} from 'pngjs';
import {prepareInk} from '../lib/processing.js';
import {classifyStrokeGroup,filterSignatureGroups,isolateSignatureMask,strokeComponents,suppressAnnotationRows} from '../lib/signatures.js';

function fixtureMask(name) {
  const png=PNG.sync.read(readFileSync(new URL('./fixtures-v3/'+name,import.meta.url)));
  return {width:png.width,height:png.height,mask:Uint8Array.from({length:png.width*png.height},(_,i)=>png.data[i*4]>=64?1:0)};
}
function contextFor(image,extra={}) {return {...image,textHeight:18,components:strokeComponents(image.mask,image.width,image.height),...extra};}
function candidateGroups(context) {return context.components.boxes.filter(c=>c.w>25&&c.h>10&&c.area>35).map(c=>({...c,members:[c.id]}));}
function blank(width=640,height=420) {return {width,height,mask:new Uint8Array(width*height)};}
function line(image,x0,y0,x1,y1,radius=1) {
  const n=Math.max(1,Math.ceil(Math.hypot(x1-x0,y1-y0)*2));
  for(let step=0;step<=n;step++) {
    const x=x0+(x1-x0)*step/n,y=y0+(y1-y0)*step/n;
    for(let yy=Math.floor(y-radius);yy<=Math.ceil(y+radius);yy++)for(let xx=Math.floor(x-radius);xx<=Math.ceil(x+radius);xx++)
      if(xx>=0&&xx<image.width&&yy>=0&&yy<image.height&&(xx-x)**2+(yy-y)**2<=radius*radius)image.mask[yy*image.width+xx]=1;
  }
}
function signature(image,x=90,y=150) {
  const points=[[0,60],[33,0],[51,93],[83,13],[107,69],[140,33],[168,68],[199,40],[235,56]];
  for(let i=1;i<points.length;i++)line(image,x+points[i-1][0],y+points[i-1][1],x+points[i][0],y+points[i][1],1.5);
}

test('short printed residues never become signatures, even with no OCR words',()=>{
  for(const name of ['failures-text-only.png','failures-text-only-blue.png']) {
    const png=PNG.sync.read(readFileSync(new URL('../examples/'+name,import.meta.url)));
    const prepared=prepareInk(png),context=contextFor(prepared);
    const result=filterSignatureGroups(candidateGroups(context),context);
    assert.equal(result.accepted.length,0,name);
    assert.ok(result.rejected.length>0,'negative contains a connected printed residue large enough to tempt the old detector');
  }
});

test('a regular row of disconnected glyphs is rejected without OCR',()=>{
  const image=blank();
  for(let x=100;x<250;x+=18) {
    line(image,x,80,x,104);line(image,x,80,x+11,80);line(image,x,91,x+9,91);
  }
  const context=contextFor(image),group={x:95,y:75,w:170,h:35};
  const result=classifyStrokeGroup(group,context);
  assert.equal(result.accepted,false);assert.equal(result.reason,'regular-text');
});

test('background cursive rows are rejected, including spaced word components',()=>{
  const context=contextFor(fixtureMask('failures-rectangular.handwritten-body.mask.png'));
  const groups=candidateGroups(context),result=filterSignatureGroups(groups,context);
  assert.ok(groups.length>=6,'contains separate word-like connected groups');
  assert.equal(result.accepted.length,0);
  assert.ok(result.rejected.some(r=>r.reason==='running-handwriting'));
});

test('compact overlapping signature flourishes are retained independently of stamp position',()=>{
  for(const name of ['failures-rectangular.signature-top.mask.png','failures-rectangular.signature-bottom.mask.png','failures-form-signature.signature-loop.mask.png']) {
    const context=contextFor(fixtureMask(name),{inStamp:true});
    const result=filterSignatureGroups(candidateGroups(context),context);
    assert.equal(result.accepted.length,1,name);
    assert.ok(result.accepted[0].confidence>.85);
  }
});

test('a signature misread as a complete high-confidence OCR word stays a signature',()=>{
  const image=blank();signature(image);
  const context=contextFor(image,{textHeight:110,words:[{text:'Иванов',confidence:99,bbox:{x0:86,y0:146,x1:331,y1:249}}]});
  const result=filterSignatureGroups(candidateGroups(context),context);
  assert.equal(result.accepted.length,1);
  assert.ok(result.accepted[0].features.ocrCoverage>.9);
});

test('signature isolation excludes neighboring print inside the same export rectangle',()=>{
  const image=blank();signature(image);
  const original=new Uint8Array(image.mask);
  for(let x=100;x<290;x+=18){line(image,x,120,x,135);line(image,x,120,x+9,120);line(image,x,128,x+7,128);}
  // A detached dot within the real signature envelope belongs to the signature.
  line(image,150,200,151,200,1);
  const result=isolateSignatureMask(image.mask,image.width,image.height,{x:70,y:100,w:290,h:170},{textHeight:18,
    words:[{x:95,y:116,w:210,h:23,text:'Обычный текст',confidence:99}]});
  for(let i=0;i<original.length;i++)if(original[i])assert.equal(result.mask[i],1,'source flourish pixel retained');
  assert.equal(result.mask[125*image.width+100],0,'printed glyph removed');
  assert.equal(result.mask[200*image.width+150],1,'detached pen dot retained');
  assert.ok(result.removedPixels>300);assert.ok(result.keptPixels>1000);
});

test('straight form rules and uncertain tiny fragments are rejected',()=>{
  const image=blank();line(image,40,50,580,65,1.2);line(image,50,200,75,210,1);
  const context=contextFor(image),result=filterSignatureGroups(context.components.boxes,context);
  assert.equal(result.accepted.length,0);assert.ok(result.rejected.some(r=>r.reason==='straight-rule'));
});

test('two independent wide signatures on the same baseline remain separate',()=>{
  const image=blank(1000,420);signature(image,80,150);signature(image,620,150);
  const context=contextFor(image),result=filterSignatureGroups(candidateGroups(context),context);
  assert.equal(result.accepted.length,2);
});

test('annotation suppression follows visible signature paths through same-color crossings',()=>{
  const top=fixtureMask('failures-rectangular.signature-top.mask.png');
  const bottom=fixtureMask('failures-rectangular.signature-bottom.mask.png');
  const background=fixtureMask('failures-rectangular.handwritten-body.mask.png');
  const mask=Uint8Array.from(top.mask,(ink,i)=>ink|bottom.mask[i]|background.mask[i]),original=new Uint8Array(mask);
  const result=suppressAnnotationRows(mask,top.width,top.height,{textHeight:18});
  const retained=layer=>{
    let total=0,kept=0;
    for(let i=0;i<mask.length;i++)if(layer.mask[i]){total++;if(result.mask[i])kept++;}
    return kept/total;
  };
  assert.ok(retained(top)>.94,'upper signature retains over 94% of measured ink');
  assert.ok(retained(bottom)>.88,'lower signature retains over 88% through dense crossings');
  assert.ok(retained(background)<.06,'under 6% of the long annotation remains');
  assert.ok(result.tracedPaths>0&&result.uncertainPixels>0,'shared crossings explicitly remain uncertain');
  assert.deepEqual(mask,original,'caller source remains unchanged');
  for(let i=0;i<mask.length;i++)if(result.mask[i])assert.equal(mask[i],1,'separation never synthesizes new pixels');
});

test('signature-only source is untouched by annotation-row suppression',()=>{
  const source=fixtureMask('failures-form-signature.signature-loop.mask.png');
  const result=suppressAnnotationRows(source.mask,source.width,source.height,{textHeight:18});
  assert.deepEqual(result.mask,source.mask);
  assert.equal(result.removedPixels,0);assert.equal(result.rows.length,0);
});
