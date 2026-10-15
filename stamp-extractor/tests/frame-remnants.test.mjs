import test from 'node:test';
import assert from 'node:assert/strict';
import { renderExtractedRegion, scaleAnalysis } from '../lib/extraction.js';

// Independently authored source/ink masks: a cursive loop runs within two pixels
// of a tilted stamp border. Separate short border pieces are accidental selected
// residue; a detached pen flourish away from the border is genuine desired ink.
function scene() {
  const width=360,height=220,size=width*height;
  const signature=new Uint8Array(size),fragments=new Uint8Array(size),flourish=new Uint8Array(size),frameInk=new Uint8Array(size);
  const blue=[24,47,150];
  function dot(mask,x,y,radius=1) {
    for(let yy=Math.floor(y-radius);yy<=Math.ceil(y+radius);yy++)for(let xx=Math.floor(x-radius);xx<=Math.ceil(x+radius);xx++)
      if(xx>=0&&yy>=0&&xx<width&&yy<height&&(xx-x)**2+(yy-y)**2<=radius*radius)mask[yy*width+xx]=1;
  }
  function line(mask,x0,y0,x1,y1,radius=1) {
    const steps=Math.ceil(Math.hypot(x1-x0,y1-y0)*4);
    for(let n=0;n<=steps;n++)dot(mask,x0+(x1-x0)*n/steps,y0+(y1-y0)*n/steps,radius);
  }
  function curve(mask,points) {
    for(let n=0;n<=600;n++) {
      const t=n/600,s=1-t;
      dot(mask,s*s*s*points[0][0]+3*s*s*t*points[1][0]+3*s*t*t*points[2][0]+t*t*t*points[3][0],
        s*s*s*points[0][1]+3*s*s*t*points[1][1]+3*s*t*t*points[2][1]+t*t*t*points[3][1]);
    }
  }
  const quad=[{x:40,y:30},{x:320,y:50},{x:320,y:190},{x:40,y:170}];
  for(let side=0;side<4;side++) {
    const a=quad[side],b=quad[(side+1)%4];line(frameInk,a.x,a.y,b.x,b.y);
  }
  const bottom=x=>170+(x-40)/14;
  line(signature,70,bottom(70)-1.5,145,bottom(145)-1.5);
  curve(signature,[[145,bottom(145)-1.5],[183,165],[157,129],[130,142]]);
  curve(signature,[[130,142],[91,161],[133,182],[188,153]]);
  line(fragments,240,bottom(240),285,bottom(285));
  line(fragments,320,85,320,111);
  // A short, detached, straight pen flourish parallel to the bottom border.
  line(flourish,235,145,278,145+43/14);
  const data=new Uint8ClampedArray(size*4);data.fill(255);
  const selectionMask=new Uint8Array(size);
  for(let i=0;i<size;i++) {
    if(frameInk[i]||signature[i]||flourish[i])data.set([...blue,255],i*4);
    selectionMask[i]=signature[i]||fragments[i]||flourish[i]?1:0;
  }
  const rect={x:20,y:10,w:320,h:200};
  const stamp={x:40,y:30,w:280,h:160,quad,mode:'blue',kind:'stamp',shape:'quadrilateral'};
  const region={...rect,key:'pen',kind:'signature',mode:'blue',selection:{x:0,y:0,w:width,h:height,width,height,mask:selectionMask}};
  const analysis={width,height,regions:[region],protectedStamps:[stamp],textBoxes:[],ruleBoxes:[],ocrWords:[],handwritingBoxes:[]};
  return {image:{width,height,data},analysis,rect,signature,fragments,flourish};
}

function enlarged(source,factor) {
  const {image}=source,width=image.width*factor,height=image.height*factor;
  const data=new Uint8ClampedArray(width*height*4);
  const masks=Object.fromEntries(['signature','fragments','flourish'].map(name=>[name,new Uint8Array(width*height)]));
  for(let y=0;y<height;y++)for(let x=0;x<width;x++) {
    const from=Math.floor(y/factor)*image.width+Math.floor(x/factor),to=y*width+x;
    data.set(image.data.subarray(from*4,from*4+4),to*4);
    for(const [name,mask] of Object.entries(masks))mask[to]=source[name][from];
  }
  return {image:{width,height,data},analysis:scaleAnalysis(source.analysis,factor),
    rect:Object.fromEntries(Object.entries(source.rect).map(([key,value])=>[key,value*factor])),...masks};
}

function retained(rendered,source,mask) {
  let total=0,kept=0;
  for(let y=0;y<source.image.height;y++)for(let x=0;x<source.image.width;x++) {
    if(!mask[y*source.image.width+x])continue;
    total++;
    const xx=x-source.rect.x,yy=y-source.rect.y;
    if(xx<0||yy<0||xx>=rendered.width||yy>=rendered.height)continue;
    const p=(yy*rendered.width+xx)*4;
    if(Math.min(rendered.data[p],rendered.data[p+1],rendered.data[p+2])<230)kept++;
  }
  return kept/total;
}

function render(source,isolate=true) {
  return renderExtractedRegion(source.image,source.rect,{kind:'signature',mode:'blue',objectKey:'pen',isolate,repair:false},[],source.analysis);
}

test('signature output removes detached tilted and vertical stamp-edge fragments',()=>{
  const source=scene(),output=render(source);
  assert.ok(retained(output,source,source.fragments)<.05,'detached known border fragments remain visible');
  assert.ok(output.removedRulePixels>50,'border cleanup should remove actual source pixels');
});

test('cursive signature close and parallel to a stamp edge remains intact',()=>{
  const source=scene(),before=new Uint8ClampedArray(source.image.data),output=render(source);
  assert.ok(retained(output,source,source.signature)>.98,'a border-wide erasure cut genuine nearby cursive ink');
  assert.deepEqual(source.image.data,before,'rendering must not change original image');
});

test('detached genuine pen flourish away from the frame survives straight-fragment cleanup',()=>{
  const source=scene(),output=render(source);
  assert.ok(retained(output,source,source.flourish)>.98,'short straight pen flourish was mistaken for a stamp edge');
  const unisolated=render(source,false);
  assert.ok(retained(unisolated,source,source.fragments)>.98,'disabling isolation must retain original border fragments');
});

test('original-resolution rendering preserves the pen and removes scaled frame remnants',()=>{
  const source=enlarged(scene(),2),output=render(source);
  assert.ok(retained(output,source,source.signature)>.98,'scaled nearby cursive ink was cut');
  assert.ok(retained(output,source,source.flourish)>.98,'scaled detached pen flourish was erased');
  assert.ok(retained(output,source,source.fragments)<.05,'scaled detached stamp-edge fragments remain visible');
});
