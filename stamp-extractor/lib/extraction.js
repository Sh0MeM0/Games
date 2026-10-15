/** Document extraction: OCR suppresses printed words; measured ink geometry
 * protects closed seal borders and connected handwriting. No missing ink is
 * reconstructed. The detector deliberately leaves ambiguous overlaps editable.
 */
import { prepareInk, cropImage, normalizeRect, maxFilter, applyStrokes } from './processing.js';

const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
const area = r => r.w * r.h;
const contains = (r, x, y, pad = 0) => x >= r.x - pad && x < r.x + r.w + pad && y >= r.y - pad && y < r.y + r.h + pad;
const intersection = (a, b) => Math.max(0, Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x)) * Math.max(0,Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y));
const union = (a, b) => ({ x:Math.min(a.x,b.x), y:Math.min(a.y,b.y), w:Math.max(a.x+a.w,b.x+b.w)-Math.min(a.x,b.x), h:Math.max(a.y+a.h,b.y+b.h)-Math.min(a.y,b.y) });
const median = values => values.length ? [...values].sort((a,b)=>a-b)[Math.floor(values.length/2)] : 18;

function connected(mask, width, height) {
  const labels = new Int32Array(mask.length), queue = new Int32Array(mask.length), boxes = [];
  let id = 0;
  for (let start = 0; start < mask.length; start++) {
    if (!mask[start] || labels[start]) continue;
    id++; let head=0, tail=1, left=start%width, right=left, top=Math.floor(start/width), bottom=top;
    let sx=0,sy=0,sxx=0,syy=0,sxy=0;
    queue[0]=start; labels[start]=id;
    while(head<tail) {
      const index=queue[head++], x=index%width, y=Math.floor(index/width);
      sx+=x;sy+=y;sxx+=x*x;syy+=y*y;sxy+=x*y;
      left=Math.min(left,x); right=Math.max(right,x); top=Math.min(top,y); bottom=Math.max(bottom,y);
      for(let dy=-1;dy<=1;dy++) for(let dx=-1;dx<=1;dx++) {
        const nx=x+dx,ny=y+dy,n=ny*width+nx;
        if(nx>=0&&nx<width&&ny>=0&&ny<height&&mask[n]&&!labels[n]) { labels[n]=id; queue[tail++]=n; }
      }
    }
    const cx=sx/tail,cy=sy/tail,vx=sxx/tail-cx*cx,vy=syy/tail-cy*cy,cov=sxy/tail-cx*cy;
    const spread=Math.sqrt((vx-vy)**2+4*cov*cov),minor=Math.max(0,(vx+vy-spread)/2),major=Math.max(1,(vx+vy+spread)/2);
    boxes.push({x:left,y:top,w:right-left+1,h:bottom-top+1,area:tail,id,cx,cy,vx,vy,cov,minor,linearity:minor/major});
  }
  return {labels,boxes};
}

function inkClass(r,g,b) {
  if(b-r>12&&b-g>4) return 1;
  if(r-g>22&&r-b>15) return 2;
  if(Math.max(r,g,b)-Math.min(r,g,b)>38) return 3;
  return 0;
}
const modes = ['black','blue','red','colored'];

function normalizeWords(words,width,height) {
  const result=[];
  for(const word of words||[]) {
    const b=word.bbox||word;
    const x=Number(b.x0??b.x),y=Number(b.y0??b.y),w=Number(b.x1!=null?b.x1-x:b.w),h=Number(b.y1!=null?b.y1-y:b.h);
    const text=String(word.text||'').trim(),confidence=Number(word.confidence??word.conf??0);
    if(![x,y,w,h,confidence].every(Number.isFinite)||w<=0||h<=0||confidence<30||!/[\p{L}\p{N}]/u.test(text))continue;
    if(w>width*.9||h>height*.15)continue;
    try { result.push({...normalizeRect({x:x-1,y:y-1,w:w+2,h:h+2},width,height),text,confidence}); } catch { /* invalid OCR box */ }
  }
  return result;
}

function atStamp(stamp,x,y) {
  if(!contains(stamp,x,y,2))return false;
  if(stamp.shape==='ellipse') {
    const nx=(x-stamp.x-stamp.w/2)/(stamp.w/2+3),ny=(y-stamp.y-stamp.h/2)/(stamp.h/2+3);
    return nx*nx+ny*ny<=1.09;
  }
  return true;
}

function stampGeometry(box,mask,width) {
  const sectors=new Uint8Array(48),edges=[new Set(),new Set(),new Set(),new Set()];
  let ringInk=0,insideInk=0;
  const cx=box.x+(box.w-1)/2,cy=box.y+(box.h-1)/2,rx=(box.w-1)/2,ry=(box.h-1)/2;
  for(let y=box.y;y<box.y+box.h;y++)for(let x=box.x;x<box.x+box.w;x++) {
    if(!mask[y*width+x])continue;
    const nx=(x-cx)/rx,ny=(y-cy)/ry,r=Math.hypot(nx,ny);
    if(r>=.84&&r<=1.08) { sectors[Math.floor(((Math.atan2(ny,nx)+Math.PI)/(2*Math.PI))*48)%48]=1;ringInk++; }
    if(r<.7)insideInk++;
    if(y<box.y+Math.max(3,box.h*.05))edges[0].add(Math.floor((x-box.x)/box.w*32));
    if(y>=box.y+box.h-Math.max(3,box.h*.05))edges[1].add(Math.floor((x-box.x)/box.w*32));
    if(x<box.x+Math.max(3,box.w*.05))edges[2].add(Math.floor((y-box.y)/box.h*32));
    if(x>=box.x+box.w-Math.max(3,box.w*.05))edges[3].add(Math.floor((y-box.y)/box.h*32));
  }
  const coverage=sectors.reduce((a,b)=>a+b,0)/48;
  const rectangular=Math.min(...edges.map(s=>s.size/32));
  if(coverage>=.79&&ringInk>Math.min(box.w,box.h)*1.3&&box.w/box.h>.5&&box.w/box.h<1.9)
    return {shape:'ellipse',confidence:Math.min(.99,.75+coverage*.22),coverage,insideInk};
  if(rectangular>=.8&&box.w/box.h<4.5&&box.w/box.h>.35&&insideInk>12)
    return {shape:'rectangle',confidence:.86,coverage:rectangular,insideInk};
  return null;
}

function findStamps(mask,width,height,mode) {
  // Small closing connects broken border fragments; unlike a wide dilation it
  // does not join every word or combine unrelated neighboring signatures.
  const expanded=maxFilter(mask,width,height,2);
  const closed=connected(expanded,width,height),stamps=[];
  for(const dilated of closed.boxes) {
    if(dilated.w<38||dilated.h<32||dilated.w>width*.8||dilated.h>height*.6||dilated.w/dilated.h>5)continue;
    let left=width,right=-1,top=height,bottom=-1,count=0;
    for(let y=dilated.y;y<dilated.y+dilated.h;y++)for(let x=dilated.x;x<dilated.x+dilated.w;x++) {
      const i=y*width+x;
      if(mask[i]&&closed.labels[i]===dilated.id){left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);count++;}
    }
    const box={x:left,y:top,w:right-left+1,h:bottom-top+1};
    if(box.w<36||box.h<30||count/area(box)>.65)continue;
    const geometry=stampGeometry(box,mask,width);
    if(!geometry)continue;
    // Large neutral rectangular frames are usually table cells/page borders.
    if(mode==='black'&&geometry.shape==='rectangle'&&(box.w>width*.4||box.h>height*.25))continue;
    stamps.push({...box,...geometry,mode,kind:'stamp'});
  }
  return stamps;
}

function findRules(mask,width,height,protectedStamps) {
  const rules=[],minimum=Math.max(100,Math.round(width*.25));
  for(let y=0;y<height;y++) {
    let start=-1;
    for(let x=0;x<=width;x++) {
      if(x<width&&mask[y*width+x]){if(start<0)start=x;continue;}
      if(start>=0&&x-start>=minimum&&!protectedStamps.some(s=>atStamp(s,(start+x)/2,y)))rules.push({x:start,y:y-1,w:x-start,h:3});
      start=-1;
    }
  }
  const minimumV=Math.max(130,Math.round(height*.22));
  for(let x=0;x<width;x++) {
    let start=-1;
    for(let y=0;y<=height;y++) {
      if(y<height&&mask[y*width+x]){if(start<0)start=y;continue;}
      if(start>=0&&y-start>=minimumV&&!protectedStamps.some(s=>atStamp(s,x,(start+y)/2)))rules.push({x:x-1,y:start,w:3,h:y-start});
      start=-1;
    }
  }
  // Neighboring rows/columns of the same printed rule form a single box.
  const merged=[];
  for(const rule of rules) {
    const existing=merged.find(r=>intersection({...r,x:r.x-1,y:r.y-1,w:r.w+2,h:r.h+2},rule)>0&&((r.w>r.h)===(rule.w>rule.h)));
    if(existing)Object.assign(existing,union(existing,rule));else merged.push({...rule});
  }
  // Photographs and rotated scans also contain slanted form rules. Principal
  // component variance measures a truly straight connected stroke; a cursive
  // signature has measurable spread around its principal axis.
  for(const c of connected(mask,width,height).boxes) {
    if(c.area<80||c.linearity>.0015||(c.w<minimum&&c.h<minimumV))continue;
    if(protectedStamps.some(s=>atStamp(s,c.cx,c.cy)))continue;
    const angle=.5*Math.atan2(2*c.cov,c.vx-c.vy);
    merged.push({x:c.x-2,y:c.y-2,w:c.w+4,h:c.h+4,line:{cx:c.cx,cy:c.cy,dx:Math.cos(angle),dy:Math.sin(angle),radius:Math.max(2,Math.sqrt(c.minor)*3+1)}});
  }
  return merged;
}

function inRule(rule,x,y) {
  if(!rule.line)return true;
  const l=rule.line;return Math.abs((x-l.cx)*l.dy-(y-l.cy)*l.dx)<=l.radius;
}

function handwritingSeed(box,textHeight,mode) {
  const minimumWidth=mode==='black'?Math.max(38,textHeight*2.1):Math.max(28,textHeight*1.3);
  return box.w>=minimumWidth&&box.h>=Math.max(12,textHeight*.65)&&box.area>=35&&box.area/area(box)<.55&&box.w/box.h>.55&&box.w/box.h<18&&box.linearity>.003;
}

function printedGlyph(box,words) {
  return words.some(word=>word.confidence>=45&&word.text.replace(/[^\p{L}\p{N}]/gu,'').length>=2&&
    intersection(word,box)>area(box)*.8&&box.w<word.w*.65&&box.h<word.h*1.4);
}

function paintBox(mask,width,height,box,value,allow) {
  const x0=Math.max(0,Math.floor(box.x)),y0=Math.max(0,Math.floor(box.y)),x1=Math.min(width,Math.ceil(box.x+box.w)),y1=Math.min(height,Math.ceil(box.y+box.h));
  for(let y=y0;y<y1;y++)for(let x=x0;x<x1;x++)if(!allow||allow(x,y))mask[y*width+x]=value;
}

function textLooksPrinted(box,components,width,mask) {
  // Typed words normally comprise several short disconnected glyphs. A long,
  // connected cursive stroke crossing a word box is protected independently of
  // Tesseract confidence, so readable handwritten names are not simply erased.
  const seen=new Set();let longest=0,covered=0;
  for(let y=box.y;y<box.y+box.h;y++)for(let x=box.x;x<box.x+box.w;x++) {
    const i=y*width+x,id=components.labels[i];if(!mask[i]||!id||seen.has(id))continue;
    seen.add(id);const c=components.boxes[id-1];longest=Math.max(longest,c.w);covered++;
  }
  return {glyphs:covered,longest};
}

export function analyzeDocument(image,words=[],options={}) {
  const {width,height}=image,prepared=prepareInk(image,{...options,mode:'all',speckle:options.speckle??3});
  const channels=Array.from({length:4},()=>new Uint8Array(width*height));
  for(let i=0;i<prepared.mask.length;i++)if(prepared.mask[i]) { const p=i*4;channels[inkClass(prepared.data[p],prepared.data[p+1],prepared.data[p+2])][i]=1; }
  const textBoxes=normalizeWords(words,width,height),textHeight=median(textBoxes.filter(b=>b.confidence>50).map(b=>b.h));
  let stamps=[];
  for(let channel=0;channel<4;channel++)stamps.push(...findStamps(channels[channel],width,height,modes[channel]));
  // A large printed O/0 belongs to its recognized word, not to a seal. Require
  // several OCR characters so a real circular seal misread as "O" survives.
  stamps=stamps.filter(stamp=>!textBoxes.some(word=>word.confidence>=50&&word.text.replace(/[^\p{L}\p{N}]/gu,'').length>=2&&
    intersection(word,stamp)>area(stamp)*.75&&stamp.w<word.w*.7&&stamp.h<textHeight*2));
  stamps.sort((a,b)=>area(b)-area(a));
  stamps=stamps.filter((stamp,index,list)=>!list.slice(0,index).some(outer=>outer.mode===stamp.mode&&intersection(outer,stamp)>area(stamp)*.85));
  const ruleBoxes=findRules(channels[0],width,height,stamps);
  const signatureRegions=[],handwritingBoxes=[];
  let removedTextPixels=0,removedRulePixels=0;
  for(let channel=0;channel<4;channel++) {
    const mode=modes[channel],mask=channels[channel];
    const original=connected(mask,width,height),protectedIds=new Set();
    for(const c of original.boxes)if(handwritingSeed(c,textHeight,mode)&&!printedGlyph(c,textBoxes)&&!stamps.some(s=>intersection(s,c)>area(c)*.65)) {
      // Do not protect page/table lines joined to text as handwriting.
      if(ruleBoxes.some(r=>intersection(r,c)>area(r)*.7&&r.w>c.w*.8))continue;
      protectedIds.add(c.id);
    }
    for(const box of textBoxes) {
      const shape=textLooksPrinted(box,original,width,mask);
      if(shape.glyphs===0)continue;
      paintBox(mask,width,height,box,0,(x,y)=>{
        if(stamps.some(s=>s.mode===mode&&atStamp(s,x,y)))return false;
        if(protectedIds.has(original.labels[y*width+x]))return false;
        if(mask[y*width+x])removedTextPixels++;
        return true;
      });
    }
    if(channel===0)for(const box of ruleBoxes)paintBox(mask,width,height,box,0,(x,y)=>{
      if(!inRule(box,x,y))return false;
      if(stamps.some(s=>s.mode===mode&&atStamp(s,x,y)))return false;
      if(mask[y*width+x])removedRulePixels++;return true;
    });
    // Seal contents are exported with their frame, never as extra signatures.
    for(const stamp of stamps)if(stamp.mode===mode)paintBox(mask,width,height,stamp,0,(x,y)=>atStamp(stamp,x,y));
    const residual=connected(mask,width,height),seeds=residual.boxes.filter(c=>handwritingSeed(c,textHeight,mode));
    const groups=[];
    for(const seed of seeds.sort((a,b)=>b.area-a.area)) {
      const nearby=groups.find(g=>{
        const gapX=Math.max(0,Math.max(seed.x-g.x-g.w,g.x-seed.x-seed.w));
        const gapY=Math.max(0,Math.max(seed.y-g.y-g.h,g.y-seed.y-seed.h));
        return gapX<Math.min(32,Math.max(g.h,seed.h)*.3)&&gapY<Math.min(15,Math.max(g.h,seed.h)*.18)&&union(g,seed).w<Math.max(g.w,seed.w)*2.3;
      });
      if(nearby){const joined=union(nearby,seed);Object.assign(nearby,joined);nearby.members.push(seed.id);nearby.ink+=seed.area;}
      else groups.push({...seed,members:[seed.id],ink:seed.area});
    }
    for(const group of groups) {
      const margin=Math.min(22,Math.max(5,group.h*.18)),neighborhood={x:group.x-margin,y:group.y-margin,w:group.w+margin*2,h:group.h+margin*2};
      for(const c of residual.boxes)if(!group.members.includes(c.id)&&c.area>=3&&c.w<group.w*.45&&intersection(neighborhood,c)>area(c)*.8)Object.assign(group,union(group,c));
      const conf=mode==='black'?.78:.87;
      const rect=normalizeRect({x:group.x-6,y:group.y-6,w:group.w+12,h:group.h+12},width,height);
      signatureRegions.push({...rect,kind:'signature',mode,confidence:conf});
      handwritingBoxes.push({...rect,mode});
    }
  }
  const regions=[...stamps.map(s=>({...normalizeRect({x:s.x-6,y:s.y-6,w:s.w+12,h:s.h+12},width,height),kind:'stamp',mode:s.mode,confidence:s.confidence,shape:s.shape})),...signatureRegions];
  regions.sort((a,b)=>a.y-b.y||a.x-b.x);
  const selected=regions.filter(r=>!options.mode||options.mode==='all'||options.mode===r.mode||(options.mode==='colored'&&r.mode!=='black'));
  return {width,height,regions:selected.slice(0,80),textBoxes,ruleBoxes,protectedStamps:stamps,handwritingBoxes,
    stats:{ocrWords:textBoxes.length,removedTextPixels,removedRulePixels,stamps:stamps.length,signatures:signatureRegions.length},truncated:regions.length>80};
}

export function scaleAnalysis(analysis,scaleX,scaleY=scaleX) {
  const box=b=>{
    const result={...b,x:b.x*scaleX,y:b.y*scaleY,w:b.w*scaleX,h:b.h*scaleY};
    if(b.line){const dx=b.line.dx*scaleX,dy=b.line.dy*scaleY,length=Math.hypot(dx,dy);result.line={...b.line,cx:b.line.cx*scaleX,cy:b.line.cy*scaleY,dx:dx/length,dy:dy/length,radius:b.line.radius*Math.max(scaleX,scaleY)};}
    return result;
  };
  return {...analysis,width:Math.round(analysis.width*scaleX),height:Math.round(analysis.height*scaleY),
    regions:(analysis.regions||[]).map(box),textBoxes:(analysis.textBoxes||[]).map(box),ruleBoxes:(analysis.ruleBoxes||[]).map(box),
    protectedStamps:(analysis.protectedStamps||[]).map(box),handwritingBoxes:(analysis.handwritingBoxes||[]).map(box)};
}

export function renderExtractedRegion(image,rect,options={},strokes=[],analysis=null) {
  const region=normalizeRect(rect,image.width,image.height),prepared=prepareInk(cropImage(image,region),options);
  const width=prepared.width,height=prepared.height,mode=options.mode||'all';
  if(mode==='black')for(let i=0;i<prepared.mask.length;i++) {
    const p=i*4;
    if(inkClass(prepared.data[p],prepared.data[p+1],prepared.data[p+2])!==0)prepared.mask[i]=0;
  }
  let removedTextPixels=0,removedRulePixels=0;
  if(analysis) {
    const local=b=>({...b,x:b.x-region.x,y:b.y-region.y});
    const stamps=(analysis.protectedStamps||[]).filter(s=>intersection(s,region)>0&&(mode==='all'||mode===s.mode||(mode==='colored'&&s.mode!=='black')));
    const ownStamp=options.kind==='stamp'?stamps.find(s=>intersection(s,region)>area(s)*.7):null;
    const textHeight=median((analysis.textBoxes||[]).filter(b=>b.confidence>50).map(b=>b.h));
    const sourceComponents=connected(prepared.mask,width,height),handwrittenIds=new Set();
    for(const c of sourceComponents.boxes)if(handwritingSeed(c,textHeight,mode)) {
      const global={...c,x:c.x+region.x,y:c.y+region.y};
      if(printedGlyph(global,analysis.textBoxes||[]))continue;
      if(!(analysis.ruleBoxes||[]).some(r=>intersection(r,global)>area(r)*.7&&r.w>c.w*.8))handwrittenIds.add(c.id);
    }
    if(options.removeText!==false&&!(options.kind==='stamp'&&!ownStamp))for(const box of analysis.textBoxes||[]) {
      if(intersection(box,region)<=0)continue;
      paintBox(prepared.mask,width,height,local(box),0,(x,y)=>{
        const gx=x+region.x,gy=y+region.y;
        if(stamps.some(s=>atStamp(s,gx,gy)))return false;
        // Retain connected handwriting, not an entire rectangular word/region.
        const localIndex=y*width+x;
        if(handwrittenIds.has(sourceComponents.labels[localIndex]))return false;
        if(prepared.mask[localIndex])removedTextPixels++;
        return true;
      });
    }
    if(options.removeLines!==false)for(const box of analysis.ruleBoxes||[]) {
      if(intersection(box,region)<=0)continue;
      paintBox(prepared.mask,width,height,local(box),0,(x,y)=>{
        if(!inRule(box,x+region.x,y+region.y))return false;
        if(stamps.some(s=>atStamp(s,x+region.x,y+region.y)))return false;
        if(prepared.mask[y*width+x])removedRulePixels++;return true;
      });
    }
    if(ownStamp)for(let y=0;y<height;y++)for(let x=0;x<width;x++)if(!atStamp(ownStamp,x+region.x,y+region.y))prepared.mask[y*width+x]=0;
  }
  applyStrokes(prepared.mask,prepared.available,width,height,strokes);
  let inkPixels=0;
  for(let i=0;i<prepared.mask.length;i++) {
    if(!prepared.mask[i]){const p=i*4;prepared.data[p]=prepared.data[p+1]=prepared.data[p+2]=255;}else inkPixels++;
  }
  return {width,height,data:prepared.data,inkPixels,removedTextPixels,removedRulePixels};
}
