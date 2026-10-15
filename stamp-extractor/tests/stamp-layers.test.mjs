import test from 'node:test';
import assert from 'node:assert/strict';
import { separateStampLayers } from '../lib/stamp-layers.js';
import { readFileSync } from 'node:fs';
import { PNG } from 'pngjs';
import { prepareInk } from '../lib/processing.js';
import { detectStampFrames, extractStampMask } from '../lib/stamps.js';

function scene(angle=0) {
  const width=470,height=270,mask=new Uint8Array(width*height),printed=new Uint8Array(mask.length),pen=new Uint8Array(mask.length);
  const cosine=Math.cos(angle),sine=Math.sin(angle),point=(u,v)=>({x:55+u*cosine-v*sine,y:42+u*sine+v*cosine});
  function line(target,u0,v0,u1,v1,radius=1) {
    const a=point(u0,v0),b=point(u1,v1),steps=Math.ceil(Math.hypot(b.x-a.x,b.y-a.y)*3);
    for(let n=0;n<=steps;n++) {
      const x=a.x+(b.x-a.x)*n/steps,y=a.y+(b.y-a.y)*n/steps;
      for(let yy=Math.floor(y-radius);yy<=Math.ceil(y+radius);yy++)for(let xx=Math.floor(x-radius);xx<=Math.ceil(x+radius);xx++)
        if(xx>=0&&xx<width&&yy>=0&&yy<height&&(xx-x)**2+(yy-y)**2<=radius**2)target[yy*width+xx]=1;
    }
  }
  for(const [a,b,c,d] of [[0,0,350,0],[350,0,350,170],[350,170,0,170],[0,170,0,0]])line(printed,a,b,c,d,1.2);
  const words=[];
  for(const top of [18,53,88]) {
    const symbols=[];
    for(let glyph=0;glyph<15;glyph++) {
      const x=24+glyph*20;
      line(printed,x,top,x,top+18);line(printed,x,top,x+10,top);line(printed,x,top+9,x+8,top+9);
      const a=point(x-2,top-2),b=point(x+12,top+20);
      symbols.push({text:'Г',confidence:97,bbox:{x0:Math.min(a.x,b.x),y0:Math.min(a.y,b.y),x1:Math.max(a.x,b.x),y1:Math.max(a.y,b.y)}});
    }
    const a=point(20,top-3),b=point(325,top+23);
    words.push({text:'СОГЛАСОВАНОТЕКСТ',confidence:97,bbox:{x0:Math.min(a.x,b.x),y0:Math.min(a.y,b.y),x1:Math.max(a.x,b.x),y1:Math.max(a.y,b.y)},symbols});
  }
  // A connected lower signature is separate from every printed glyph.
  const signature=[[24,145],[75,116],[44,156],[130,122],[97,150],[175,129],[153,155],[300,131]];
  for(let i=1;i<signature.length;i++)line(pen,...signature[i-1],...signature[i],.9);
  const frame={x:40,y:20,w:390,h:210,quad:[point(0,0),point(350,0),point(350,170),point(0,170)],shape:'quadrilateral'};
  const rebuild=()=>{for(let i=0;i<mask.length;i++)mask[i]=printed[i]||pen[i];};rebuild();
  return {width,height,mask,printed,pen,words,frame,line,point,rebuild};
}
function recall(actual,expected,exclude=null) {
  let total=0,kept=0;
  for(let i=0;i<expected.length;i++)if(expected[i]&&!exclude?.[i]){total++;if(actual[i])kept++;}
  return total?kept/total:1;
}

test('printed stamp layer keeps every letter and border while separating its connected signature',()=>{
  const s=scene(),original=s.mask.slice(),out=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words);
  assert.ok(recall(out.stampMask,s.printed)>.995);
  assert.ok(recall(out.stampMask,s.pen,s.printed)<.005,'pen signature does not remain in stamp file');
  assert.ok(recall(out.handwritingMask,s.pen)>.995,'separate pen layer keeps signature');
  assert.ok(out.stats.regularRows>=3);
  assert.deepEqual(s.mask,original,'source mask stays unchanged');
});

test('missing OCR does not erase stamp inscriptions',()=>{
  const s=scene(),out=separateStampLayers(s.mask,s.width,s.height,s.frame,[]);
  assert.ok(recall(out.stampMask,s.printed)>.995);
  assert.ok(recall(out.handwritingMask,s.pen)>.995);
});

test('rotated frame uses its own axes for type rows',()=>{
  const s=scene(-.09),out=separateStampLayers(s.mask,s.width,s.height,s.frame,[]);
  assert.ok(recall(out.stampMask,s.printed)>.99);
  assert.ok(recall(out.stampMask,s.pen,s.printed)<.01);
});

test('narrow analysis border keeps a nearby parallel pen stroke available without dropping the frame',()=>{
  const s=scene();
  // A long pen flourish four pixels inside a thin border must remain available
  // to signature detection. The stamp export may conservatively protect a wider
  // border, so analysis uses its explicit, narrower borderRadius option.
  s.line(s.pen,70,166,220,166,.6);s.line(s.pen,220,166,230,140,.8);s.rebuild();
  const split=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words,{borderRadius:2});
  const p=s.point(130,166),i=Math.round(p.y)*s.width+Math.round(p.x);
  assert.equal(s.pen[i],1);assert.equal(split.handwritingMask[i],1);assert.equal(split.stampMask[i],0);
  assert.ok(recall(split.stampMask,s.printed)>.995,'the observed thin frame and lettering remain protected');
});

test('whole-layer option retains original ink and never silently removes lettering',()=>{
  const s=scene(),out=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words,{stampLayer:'whole'});
  assert.equal(out.mode,'whole');
  assert.ok(recall(out.stampMask,s.printed)>.995);
  assert.ok(recall(out.stampMask,s.pen)>.995);
  assert.equal(out.stats.handwritingPixels,0);
});

test('background pen writing outside the frame is excluded from both layers',()=>{
  const s=scene();s.line(s.pen,-45,195,365,195);s.rebuild();
  const out=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words),point=s.point(100,195),index=Math.round(point.y)*s.width+Math.round(point.x);
  assert.equal(s.mask[index],1);assert.equal(out.stampMask[index],0);assert.equal(out.handwritingMask[index],0);
});

test('same-colour crossings preserve supported glyph pixels and report ambiguity without inventing pixels',()=>{
  const s=scene();
  // This stroke joins multiple printed glyphs into one component. Individual
  // symbol boxes preserve the original letters; shared pixels remain uncertain.
  for(let n=0;n<17;n++)s.line(s.pen,5+n*20,59+(n%2)*8,25+n*20,59+((n+1)%2)*8,.8);
  s.rebuild();const out=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words);
  assert.ok(recall(out.stampMask,s.printed)>.995,'OCR-supported lettering survives the crossing');
  assert.equal(out.ambiguous,true);
  assert.ok(out.stats.uncertainPixels>0);
  for(let i=0;i<s.mask.length;i++)assert.ok(!out.stampMask[i]&&!out.handwritingMask[i]||s.mask[i],'all output ink comes from source');
});

test('a cursive word incorrectly recognised by OCR is not retained as a whole word rectangle',()=>{
  const s=scene(),a=s.point(20,112),b=s.point(310,159);
  s.words.push({text:'Иванов',confidence:99,bbox:{x0:a.x,y0:a.y,x1:b.x,y1:b.y}});
  const out=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words);
  assert.ok(recall(out.stampMask,s.pen,s.printed)<.005);
});

test('a missed printed glyph between confident OCR fragments stays with the stamp, not the signature',()=>{
  const s=scene(),middle=s.words[1];
  const fragment=symbols=>({text:'ТЕКСТ'.repeat(3).slice(0,symbols.length),confidence:96,symbols,
    bbox:{x0:Math.min(...symbols.map(g=>g.bbox.x0)),y0:Math.min(...symbols.map(g=>g.bbox.y0)),
      x1:Math.max(...symbols.map(g=>g.bbox.x1)),y1:Math.max(...symbols.map(g=>g.bbox.y1))}});
  // OCR omits one character, while a pen stroke physically joins that glyph to
  // the signature. Neither an OCR label nor the reference mask is supplied for
  // the missed character; adjacent measured text fragments are the evidence.
  s.words.splice(1,1,fragment(middle.symbols.slice(0,5)),fragment(middle.symbols.slice(6)));
  s.line(s.pen,124,62,128,122,.9);s.rebuild();
  const out=separateStampLayers(s.mask,s.width,s.height,s.frame,s.words);
  let expected=0,retained=0,mislabelled=0;
  for(let y=52;y<=72;y++)for(let x=123;x<=135;x++) {
    const p=s.point(x,y),i=Math.round(p.y)*s.width+Math.round(p.x);
    if(!s.printed[i])continue;
    expected++;if(out.stampMask[i])retained++;if(out.handwritingMask[i])mislabelled++;
  }
  assert.ok(expected>30);assert.equal(retained,expected);assert.equal(mislabelled,0);
  assert.ok(out.stats.textGaps>0);assert.equal(out.ambiguous,true);
  for(let i=0;i<s.mask.length;i++)assert.ok(!out.stampMask[i]||s.mask[i],'gap protection only attributes original ink');
});

test('invalid masks and degenerate frames fail explicitly',()=>{
  assert.throws(()=>separateStampLayers(new Uint8Array(2),5,5,{x:0,y:0,w:2,h:2}),/маска/);
  assert.throws(()=>separateStampLayers(new Uint8Array(25),5,5,{quad:[{x:0,y:0},{x:0,y:0},{x:0,y:0},{x:0,y:0}]}),/рамка/);
});

test('circular seals retain their inner concentric border',()=>{
  const width=220,height=220,mask=new Uint8Array(width*height);
  for(const radius of [90,73])for(let angle=0;angle<Math.PI*2;angle+=.002) {
    const x=Math.round(110+Math.cos(angle)*radius),y=Math.round(110+Math.sin(angle)*radius);mask[y*width+x]=1;
  }
  const out=separateStampLayers(mask,width,height,{x:20,y:20,w:180,h:180,shape:'ellipse'},[]);
  assert.ok(recall(out.stampMask,mask)>.995);
  assert.equal(out.stats.handwritingPixels,0);
});

function authored(name) {
  const metadata=JSON.parse(readFileSync(new URL(`./fixtures-v3/${name}.json`,import.meta.url)));
  const image=PNG.sync.read(readFileSync(new URL(`../examples/${name}.png`,import.meta.url)));
  const maskFile=name=>PNG.sync.read(readFileSync(new URL(`./fixtures-v3/${name}`,import.meta.url)));
  const source=prepareInk(image,{mode:'blue'}),frames=detectStampFrames(source.mask,image.width,image.height,'blue');
  return {metadata,image,maskFile,source,frames};
}

test('authored tilted stamp inscriptions survive without any OCR, including Cyrillic descenders',()=>{
  const s=authored('failures-rectangular-clean');
  assert.equal(s.frames.length,2);
  for(let n=0;n<2;n++) {
    const mask=extractStampMask(s.image,s.source.mask,s.frames[n]).mask;
    const out=separateStampLayers(mask,s.image.width,s.image.height,s.frames[n],[]);
    const letters=s.maskFile(s.metadata.objects[n].letter_mask),expected=new Uint8Array(mask.length);
    for(let i=0;i<expected.length;i++)expected[i]=letters.data[i*4]>128?1:0;
    assert.ok(recall(out.stampMask,expected)>.99,`stamp ${n+1}: original lettering retained without OCR`);
    assert.equal(out.stats.handwritingPixels,0,'clean printed stamp creates no handwriting layer');
  }
});

test('authored crossing handwriting is removed while visible stamp letters remain intact',()=>{
  const s=authored('failures-rectangular');
  // Known OCR symbols isolate layer separation from OCR recognition quality.
  // The actual OCR end-to-end test is independent. Generator role labels are
  // explicitly removed; the algorithm cannot see the expected classification.
  const words=s.metadata.printed_words.map(({role,...word})=>word);
  const body=s.maskFile(s.metadata.backgrounds.find(b=>b.id==='handwritten-body').mask);
  for(let n=0;n<2;n++) {
    const mask=extractStampMask(s.image,s.source.mask,s.frames[n]).mask;
    const out=separateStampLayers(mask,s.image.width,s.image.height,s.frames[n],words);
    const letters=s.maskFile(s.metadata.objects[n].letter_mask),visible=s.maskFile(s.metadata.objects[n].visible_mask),expected=new Uint8Array(mask.length);
    for(let i=0;i<expected.length;i++)expected[i]=letters.data[i*4]>128&&visible.data[i*4]>128?1:0;
    assert.ok(recall(out.stampMask,expected)>.99,`stamp ${n+1}: visible original lettering preserved`);
    if(n===1) {
      let total=0,retained=0;const f=s.frames[n];
      for(let y=f.y;y<f.y+f.h;y++)for(let x=f.x;x<f.x+f.w;x++) {
        const i=y*s.image.width+x;
        if(body.data[i*4]<=128||letters.data[i*4]>128)continue;
        total++;if(out.stampMask[i])retained++;
      }
      assert.ok(total>1000);assert.ok(retained/total<.02,'at least 98% of distinct handwritten background removed');
    }
  }
});
