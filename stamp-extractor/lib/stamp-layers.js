/** Separate measured stamp ink from connected pen strokes.
 *
 * The frame and regular, individual printed glyphs are positive evidence for
 * stamp ink. OCR is additional evidence, never an instruction to erase a whole
 * word rectangle. Shared same-colour pixels cannot be attributed exactly: they
 * are retained where a printed glyph is supported and returned as uncertain.
 * Nothing in this module draws new pixels or replaces lettering with a font.
 */
const median = values => values.length ? [...values].sort((a,b)=>a-b)[Math.floor(values.length/2)] : 0;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));

function geometry(frame) {
  const points=frame.quad||frame.points;
  if(points?.length===4) {
    const [a,b,,d]=points,ex={x:b.x-a.x,y:b.y-a.y},ey={x:d.x-a.x,y:d.y-a.y};
    const determinant=ex.x*ey.y-ex.y*ey.x;
    if(Math.abs(determinant)<1)throw new Error('Некорректная рамка печати.');
    const width=Math.hypot(ex.x,ex.y),height=Math.hypot(ey.x,ey.y);
    return {width,height,local(x,y){
      const dx=x-a.x,dy=y-a.y;
      return {u:(dx*ey.y-dy*ey.x)/determinant*width,v:(ex.x*dy-ex.y*dx)/determinant*height};
    },signedDistance(x,y){
      let result=Infinity;
      for(let i=0;i<4;i++) {
        const p=points[i],q=points[(i+1)%4],length=Math.hypot(q.x-p.x,q.y-p.y);
        result=Math.min(result,((q.x-p.x)*(y-p.y)-(q.y-p.y)*(x-p.x))/length);
      }
      return result;
    }};
  }
  const width=frame.w,height=frame.h;
  if(!(width>0&&height>0))throw new Error('Некорректная рамка печати.');
  if(frame.shape==='ellipse')return {width,height,local:(x,y)=>({u:x-frame.x,v:y-frame.y}),signedDistance(x,y){
    const u=(x-frame.x-width/2)/(width/2),v=(y-frame.y-height/2)/(height/2);
    return (1-Math.hypot(u,v))*Math.min(width,height)/2;
  }};
  return {width,height,local:(x,y)=>({u:x-frame.x,v:y-frame.y}),signedDistance:(x,y)=>Math.min(x-frame.x,y-frame.y,frame.x+width-x,frame.y+height-y)};
}

function wordBox(word, shape) {
  const b=word.bbox||word,x=Number(b.x0??b.x),y=Number(b.y0??b.y),w=Number(b.x1!=null?b.x1-x:b.w),h=Number(b.y1!=null?b.y1-y:b.h);
  if(![x,y,w,h].every(Number.isFinite)||w<=0||h<=0)return null;
  const corners=[shape.local(x,y),shape.local(x+w,y),shape.local(x+w,y+h),shape.local(x,y+h)];
  return {x,y,w,h,u:Math.min(...corners.map(p=>p.u)),v:Math.min(...corners.map(p=>p.v)),
    uw:Math.max(...corners.map(p=>p.u))-Math.min(...corners.map(p=>p.u)),vh:Math.max(...corners.map(p=>p.v))-Math.min(...corners.map(p=>p.v)),
    text:String(word.text||''),confidence:Number(word.confidence??word.conf??0)};
}

function components(mask,width,height,shape,bounds) {
  const labels=new Int32Array(mask.length),queue=new Int32Array(Math.max(1,bounds.w*bounds.h)),items=[];
  for(let y=bounds.y;y<bounds.y+bounds.h;y++)for(let x=bounds.x;x<bounds.x+bounds.w;x++) {
    const start=y*width+x;
    if(!mask[start]||labels[start])continue;
    const id=items.length+1,pixels=[];
    let head=0,tail=1,left=Infinity,right=-Infinity,top=Infinity,bottom=-Infinity;
    labels[start]=id;queue[0]=start;
    while(head<tail) {
      const index=queue[head++],px=index%width,py=Math.floor(index/width),local=shape.local(px,py);pixels.push(index);
      left=Math.min(left,local.u);right=Math.max(right,local.u);top=Math.min(top,local.v);bottom=Math.max(bottom,local.v);
      for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++) {
        const nx=px+dx,ny=py+dy,next=ny*width+nx;
        if(nx<bounds.x||nx>=bounds.x+bounds.w||ny<bounds.y||ny>=bounds.y+bounds.h||!mask[next]||labels[next])continue;
        labels[next]=id;queue[tail++]=next;
      }
    }
    items.push({id,pixels,u:left,v:top,w:right-left+1,h:bottom-top+1,area:pixels.length});
  }
  return {items,labels};
}

function inBox(box,x,y,padding=0) { return x>=box.x-padding&&x<box.x+box.w+padding&&y>=box.y-padding&&y<box.y+box.h+padding; }
function overlapRatio(component,box) {
  return Math.max(0,Math.min(component.u+component.w,box.u+box.uw)-Math.max(component.u,box.u))*
    Math.max(0,Math.min(component.v+component.h,box.v+box.vh)-Math.max(component.v,box.v))/(component.w*component.h);
}

/**
 * prepared: { data?: RGBA, mask: Uint8Array }, or a binary mask directly.
 * Coordinates of frame and OCR boxes are in the same full image/crop as mask.
 * Returns new masks of width*height; caller chooses stampMask or handwritingMask.
 * options.stampLayer='whole' explicitly retains all measured ink in the frame.
 * Default 'printed' keeps the frame and printed glyphs, suppressing pen strokes.
 */
export function separateStampLayers(prepared,width,height,frame,words=[],options={}) {
  const mask=prepared?.mask||prepared;
  if(!mask||mask.length!==width*height||!Number.isInteger(width)||!Number.isInteger(height)||width<=0||height<=0)
    throw new Error('Некорректная маска изображения.');
  const shape=geometry(frame),stampMask=new Uint8Array(mask.length),handwritingMask=new Uint8Array(mask.length),uncertainMask=new Uint8Array(mask.length),insideMask=new Uint8Array(mask.length);
  const borderRadius=options.borderRadius??Math.max(2,Math.min(4,Math.min(shape.width,shape.height)*.018));
  const points=frame.quad||frame.points;
  const x0=clamp(Math.floor(points?Math.min(...points.map(p=>p.x))-borderRadius:frame.x-borderRadius),0,width);
  const y0=clamp(Math.floor(points?Math.min(...points.map(p=>p.y))-borderRadius:frame.y-borderRadius),0,height);
  const x1=clamp(Math.ceil(points?Math.max(...points.map(p=>p.x))+borderRadius:frame.x+frame.w+borderRadius),x0,width);
  const y1=clamp(Math.ceil(points?Math.max(...points.map(p=>p.y))+borderRadius:frame.y+frame.h+borderRadius),y0,height);
  const bounds={x:x0,y:y0,w:x1-x0,h:y1-y0};
  let inputPixels=0,borderPixels=0;
  for(let y=y0;y<y1;y++)for(let x=x0;x<x1;x++) {
    const i=y*width+x;
    if(!mask[i])continue;
    const distance=shape.signedDistance(x,y);
    if(distance < -borderRadius)continue;
    inputPixels++;
    if(distance<=borderRadius+1) {stampMask[i]=1;borderPixels++;}
    else insideMask[i]=1;
  }
  // Circular seals frequently have a second printed ring. It is another frame,
  // not a large connected handwritten component. Require angular coverage so
  // an ordinary oval pen loop is not protected just because of its position.
  if(frame.shape==='ellipse') {
    const rings=Array.from({length:64},()=>new Set());
    for(let y=y0;y<y1;y++)for(let x=x0;x<x1;x++)if(insideMask[y*width+x]) {
      const nx=(x-frame.x-shape.width/2)/(shape.width/2),ny=(y-frame.y-shape.height/2)/(shape.height/2),r=Math.hypot(nx,ny);
      if(r<.50||r>.95)continue;
      const sector=Math.floor((Math.atan2(ny,nx)+Math.PI)/(Math.PI*2)*48)%48,bin=Math.min(63,Math.floor(r*64));
      for(let offset=-1;offset<=1;offset++)if(bin+offset>=0&&bin+offset<64)rings[bin+offset].add(sector);
    }
    const supported=rings.map((sectors,bin)=>sectors.size>=44?bin:-1).filter(bin=>bin>=0);
    if(supported.length)for(let y=y0;y<y1;y++)for(let x=x0;x<x1;x++) {
      const i=y*width+x;if(!insideMask[i])continue;
      const r=Math.hypot((x-frame.x-shape.width/2)/(shape.width/2),(y-frame.y-shape.height/2)/(shape.height/2));
      if(supported.some(bin=>Math.abs(r*64-bin)<1.5)){stampMask[i]=1;insideMask[i]=0;borderPixels++;}
    }
  }
  if(options.stampLayer==='whole') {
    for(let i=0;i<insideMask.length;i++)if(insideMask[i])stampMask[i]=1;
    return {stampMask,handwritingMask,uncertainMask,stats:{inputPixels,borderPixels,printedPixels:inputPixels-borderPixels,handwritingPixels:0,uncertainPixels:0,printedComponents:0,handwritingComponents:0,regularRows:0},ambiguous:false,mode:'whole'};
  }
  const {items}=components(insideMask,width,height,shape,bounds);
  const ownWords=[],symbols=[];
  for(const word of words) {
    const box=wordBox(word,shape);
    if(!box||box.confidence<30||!/[\p{L}\p{N}]/u.test(box.text)||shape.signedDistance(box.x+box.w/2,box.y+box.h/2)<0)continue;
    ownWords.push(box);
    // Low-confidence OCR often turns cursive background writing into short
    // Latin "words". Its rectangles must not become protected stamp lettering.
    for(const symbol of box.confidence>=55?word.symbols||[]:[]) {
      const glyph=wordBox(symbol,shape);
      if(glyph&&glyph.confidence>=30&&/[\p{L}\p{N}]/u.test(glyph.text)&&glyph.uw<shape.width*.15&&glyph.vh<shape.height*.42)symbols.push(glyph);
    }
  }
  // Estimate character sizes from measured components, not the unreliable OCR
  // rectangle height (a slanted line's axis-aligned rectangle can be very tall).
  const candidate=c=>c.area>=3&&c.h>=Math.max(3,shape.height*.025)&&c.h<=Math.max(14,shape.height*.34)&&
    c.w<=Math.max(16,shape.height*.35)&&c.w/c.h<=2.2&&c.area/(c.w*c.h)>.08;
  const glyphs=items.filter(candidate);
  const typicalHeight=median(glyphs.filter(c=>c.h>=shape.height*.06).map(c=>c.h))||Math.max(8,shape.height*.14);
  const rows=[];
  for(const c of [...glyphs].sort((a,b)=>a.v+a.h-b.v-b.h)) {
    if(c.h<typicalHeight*.48||c.h>typicalHeight*1.8)continue;
    const baseline=c.v+c.h,row=rows.find(r=>Math.abs(r.baseline-baseline)<Math.max(2,Math.min(r.height,c.h)*.28));
    if(row) {row.members.push(c);row.baseline=median(row.members.map(g=>g.v+g.h));row.height=median(row.members.map(g=>g.h));}
    else rows.push({members:[c],baseline,height:c.h});
  }
  const regularRows=rows.filter(r=>r.members.length>=3&&Math.max(...r.members.map(c=>c.u+c.w))-Math.min(...r.members.map(c=>c.u))>=shape.width*.10);
  // Capitals, ascenders and descenders have different baselines/bounds in the
  // same printed word. Compare line centres as well as the common baseline;
  // otherwise Cyrillic р/у/ф get deleted while the neighbouring letters stay.
  const rowFor=c=>regularRows.find(r=>
    (Math.abs(c.v+c.h-r.baseline)<=Math.max(3,r.height*.32)||
     Math.abs(c.v+c.h/2-(r.baseline-r.height/2))<=Math.max(3,r.height*.45))&&
    c.h<=r.height*2.0&&c.h>=r.height*.45);
  const supportedWords=ownWords.filter(w=>{
    const contained=glyphs.filter(c=>overlapRatio(c,w)>.65&&c.h<w.vh*1.5);
    const chars=w.text.replace(/[^\p{L}\p{N}]/gu,'').length;
    return contained.length>=Math.max(2,Math.min(4,chars*.35))&&w.uw>typicalHeight;
  });
  // A pen stroke can make OCR split one printed word, for example "инже" +
  // "ер" with the intervening н missed. Preserve ORIGINAL ink in a short gap
  // between confident fragments of the same measured text row. This is an
  // attribution hint, not reconstructed lettering; shared pen pixels in this
  // narrow band are explicitly uncertain. Never bridge arbitrary OCR words.
  const textGaps=[];
  const fragments=ownWords.filter(w=>w.confidence>=75&&w.text.replace(/[^\p{L}\p{N}]/gu,'').length>=2).sort((a,b)=>a.u-b.u);
  for(let ai=0;ai<fragments.length;ai++)for(let bi=ai+1;bi<fragments.length;bi++) {
    const a=fragments[ai],b=fragments[bi],left=a.u+a.uw,right=b.u,gap=right-left;
    if(gap<=0||gap>typicalHeight*1.7)continue;
    if(!supportedWords.includes(a)&&!supportedWords.includes(b))continue;
    if(Math.abs((a.v+a.vh/2)-(b.v+b.vh/2))>Math.min(a.vh,b.vh)*.45)continue;
    const top=Math.max(a.v,b.v)-1,bottom=Math.min(a.v+a.vh,b.v+b.vh)+1;
    if(bottom-top<typicalHeight*.55)continue;
    if(!rowFor({v:top,h:bottom-top}))continue;
    textGaps.push({u:left-1,v:top,w:right-left+2,h:bottom-top});
  }
  let printedPixels=0,handwritingPixels=0,uncertainPixels=0,uncertainRemovedPixels=0,printedComponents=0,handwritingComponents=0;
  const probablePrinted=new Set();
  for(const c of glyphs) {
    const symbolSupport=symbols.some(s=>overlapRatio(c,s)>.7&&c.w<s.uw*1.6&&c.h<s.vh*1.5);
    const wordSupport=supportedWords.some(w=>overlapRatio(c,w)>.7);
    const rowSupport=!!rowFor(c);
    // When OCR misses a seal entirely, retain compact original glyphs instead
    // of deleting its inscription. Ambiguous isolated marks remain reviewable.
    const conservativeFallback=!regularRows.length&&!supportedWords.length&&c.w/c.h<1.7&&c.h<=typicalHeight*1.65;
    if(symbolSupport||wordSupport||rowSupport||conservativeFallback) {
      probablePrinted.add(c.id);printedComponents++;
      for(const i of c.pixels) {stampMask[i]=1;printedPixels++;if(conservativeFallback&&!symbolSupport){uncertainMask[i]=1;uncertainPixels++;}}
    }
  }
  // Periods, detached dots and small fragments of damaged letters belong to a
  // nearby printed row/letter; punctuation must not disappear with the pen ink.
  for(const c of items) {
    if(probablePrinted.has(c.id)||c.h>typicalHeight*.65||c.w>typicalHeight*.65||c.area>typicalHeight*typicalHeight*.3)continue;
    const nearGlyph=glyphs.some(g=>probablePrinted.has(g.id)&&
      Math.max(0,Math.max(g.u-c.u-c.w,c.u-g.u-g.w))<typicalHeight*.45&&
      Math.max(0,Math.max(g.v-c.v-c.h,c.v-g.v-g.h))<typicalHeight*.35);
    if(!nearGlyph)continue;
    probablePrinted.add(c.id);printedComponents++;
    for(const i of c.pixels){stampMask[i]=1;printedPixels++;}
  }
  for(const c of items) {
    if(probablePrinted.has(c.id))continue;
    handwritingComponents++;
    // A connected stroke can cross a real letter. Restrict preservation to
    // actual OCR symbol boxes, never the whole word, and report the ambiguity.
    const touchingSymbols=symbols.filter(s=>s.confidence>=45&&s.uw<=typicalHeight*2.2&&s.vh<=typicalHeight*2.1&&
      c.u<s.u+s.uw&&c.u+c.w>s.u&&c.v<s.v+s.vh&&c.v+c.h>s.v);
    const touchingGaps=textGaps.filter(g=>c.u<g.u+g.w&&c.u+c.w>g.u&&c.v<g.v+g.h&&c.v+c.h>g.v);
    const possibleMixedRow=c.w>typicalHeight*2.5&&c.h<typicalHeight*3.5&&c.area/(c.w*c.h)>.16;
    for(const i of c.pixels) {
      const x=i%width,y=Math.floor(i/width);
      const p=touchingGaps.length?shape.local(x,y):null;
      if(touchingSymbols.some(s=>inBox(s,x,y))||touchingGaps.some(g=>p.u>=g.u&&p.u<g.u+g.w&&p.v>=g.v&&p.v<g.v+g.h)) {
        stampMask[i]=1;uncertainMask[i]=1;printedPixels++;uncertainPixels++;
      } else {
        handwritingMask[i]=1;handwritingPixels++;
        // A dense joined line may contain hidden printed letters as well as a
        // pen stroke. Do not claim clean separation merely because OCR missed
        // it: mark discarded source ink for the caller's review warning too.
        if(possibleMixedRow){uncertainMask[i]=1;uncertainPixels++;uncertainRemovedPixels++;}
      }
    }
  }
  return {stampMask,handwritingMask,uncertainMask,stats:{inputPixels,borderPixels,printedPixels,handwritingPixels,uncertainPixels,uncertainRemovedPixels,printedComponents,handwritingComponents,regularRows:regularRows.length,textGaps:textGaps.length},
    ambiguous:uncertainPixels>0,mode:'printed'};
}
