/** Conservative geometry classification of ink groups.
 *
 * A connected ink group is not, by itself, a signature. These routines reject
 * ordinary glyph rows, small word fragments and extended baseline writing.
 * They report measured evidence rather than claiming handwriting recognition.
 * Pixels that cannot be separated from a connected stroke stay ambiguous.
 */
import {skeletonize} from './repair.js';
const clamp = (n, lo, hi) => Math.max(lo, Math.min(hi, n));
const area = box => Math.max(0, box.w) * Math.max(0, box.h);
const overlap = (a, b) => Math.max(0, Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x)) *
  Math.max(0, Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y));
const median = values => values.length ? [...values].sort((a,b)=>a-b)[Math.floor(values.length/2)] : 0;
const deviation = values => { const m=median(values);return median(values.map(v=>Math.abs(v-m))); };

function normalizedWords(words=[]) {
  return words.map(word=>{
    const b=word.bbox||word,x=Number(b.x0??b.x),y=Number(b.y0??b.y);
    return {...word,x,y,w:Number(b.x1!=null?b.x1-x:b.w),h:Number(b.y1!=null?b.y1-y:b.h),
      text:String(word.text||''),confidence:Number(word.confidence??word.conf??0)};
  }).filter(w=>[w.x,w.y,w.w,w.h].every(Number.isFinite)&&w.w>0&&w.h>0);
}

/** 8-connected components, compatible with extraction.js component records. */
export function strokeComponents(mask,width,height) {
  if(mask.length!==width*height)throw new Error('Размер маски штрихов не совпадает с изображением.');
  const labels=new Int32Array(mask.length),queue=new Int32Array(mask.length),boxes=[];
  for(let start=0;start<mask.length;start++) {
    if(!mask[start]||labels[start])continue;
    const id=boxes.length+1;let head=0,tail=1,left=start%width,right=left,top=Math.floor(start/width),bottom=top;
    queue[0]=start;labels[start]=id;
    let sx=0,sy=0,sxx=0,syy=0,sxy=0;
    while(head<tail) {
      const i=queue[head++],x=i%width,y=Math.floor(i/width);
      left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);
      sx+=x;sy+=y;sxx+=x*x;syy+=y*y;sxy+=x*y;
      for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++) {
        const xx=x+dx,yy=y+dy,n=yy*width+xx;
        if(xx<0||xx>=width||yy<0||yy>=height||!mask[n]||labels[n])continue;
        labels[n]=id;queue[tail++]=n;
      }
    }
    const cx=sx/tail,cy=sy/tail,vx=sxx/tail-cx*cx,vy=syy/tail-cy*cy,cov=sxy/tail-cx*cy;
    const spread=Math.hypot(vx-vy,2*cov),minor=Math.max(0,(vx+vy-spread)/2),major=Math.max(1,(vx+vy+spread)/2);
    boxes.push({id,x:left,y:top,w:right-left+1,h:bottom-top+1,area:tail,cx,cy,vx,vy,cov,minor,linearity:minor/major});
  }
  return {labels,boxes};
}

function groupFeatures(group,context) {
  const {mask,width,height}=context;
  if(!mask||mask.length!==width*height)throw new Error('Для классификации нужна маска исходных штрихов.');
  const components=context.components||strokeComponents(mask,width,height);
  const ids=group.members?.length ? new Set(group.members) : null;
  const parts=components.boxes.filter(c=>ids?ids.has(c.id):overlap(c,group)>area(c)*.75);
  const partIds=new Set(parts.map(c=>c.id));
  const x0=clamp(Math.floor(group.x),0,width),x1=clamp(Math.ceil(group.x+group.w),0,width);
  const y0=clamp(Math.floor(group.y),0,height),y1=clamp(Math.ceil(group.y+group.h),0,height);
  const words=normalizedWords(context.words||context.textBoxes).filter(w=>w.confidence>=40&&overlap(w,group)>0);
  let ink=0,ocrInk=0,left=width,right=-1,top=height,bottom=-1;
  let sx=0,sy=0,sxx=0,syy=0,sxy=0;
  const bins=Array.from({length:16},()=>({min:height,max:-1,ink:0}));
  for(let y=y0;y<y1;y++)for(let x=x0;x<x1;x++) {
    const i=y*width+x;
    if(!mask[i]||!partIds.has(components.labels[i]))continue;
    ink++;sx+=x;sy+=y;sxx+=x*x;syy+=y*y;sxy+=x*y;
    left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);
    const bin=bins[Math.min(15,Math.floor((x-x0)/Math.max(1,x1-x0)*16))];
    bin.min=Math.min(bin.min,y);bin.max=Math.max(bin.max,y);bin.ink++;
    if(words.some(w=>x>=w.x&&x<w.x+w.w&&y>=w.y&&y<w.y+w.h))ocrInk++;
  }
  if(!ink)return {ink:0,parts,components,words};
  const bounds={x:left,y:top,w:right-left+1,h:bottom-top+1},aspect=bounds.w/bounds.h;
  const cx=sx/ink,cy=sy/ink,vx=sxx/ink-cx*cx,vy=syy/ink-cy*cy,cov=sxy/ink-cx*cy;
  const spread=Math.hypot(vx-vy,2*cov),minor=Math.max(0,(vx+vy-spread)/2),major=Math.max(1,(vx+vy+spread)/2);
  const significant=parts.filter(c=>c.area>=Math.max(6,ink*.009)&&c.h>=bounds.h*.18);
  const biggest=[...parts].sort((a,b)=>b.area-a.area)[0];
  const glyphs=significant.filter(c=>c.h>=bounds.h*.45&&c.w<c.h*1.65);
  const heights=glyphs.map(c=>c.h),baselines=glyphs.map(c=>c.y+c.h);
  const glyphHeight=median(heights);
  const regularGlyphs=glyphs.length>=3&&deviation(heights)<glyphHeight*.23&&deviation(baselines)<glyphHeight*.2;
  const occupied=bins.filter(b=>b.ink>0),spans=occupied.map(b=>b.max-b.min+1);
  const envelopeUniformity=occupied.length>=8?1-clamp(deviation(spans)/Math.max(1,median(spans)),0,1):0;
  const tallBinFraction=occupied.length?occupied.filter(b=>b.max-b.min+1>bounds.h*.65).length/occupied.length:0;
  // A single OCR box can cover an entire cursive signature. It must not inflate
  // the page's estimated text size until all handwriting becomes "small text".
  const rawTextHeight=Number(context.textHeight)||median(words.map(w=>w.h))||18;
  const textHeight=clamp(rawTextHeight,8,Math.max(18,Math.min(height*.055,42)));
  const alphanumeric=word=>word.text.replace(/[^\p{L}\p{N}]/gu,'').length;
  const readableWords=words.filter(w=>w.confidence>=55&&alphanumeric(w)>=2&&overlap(w,bounds)>area(w)*.45);
  return {ink,bounds,parts,components,words,aspect,textHeight,linearity:minor/major,density:ink/area(bounds),
    ocrCoverage:ocrInk/ink,readableWords:readableWords.length,
    longestSpan:Math.max(0,...parts.map(c=>c.w))/bounds.w,
    largestFraction:(biggest?.area||0)/ink,largestHeight:(biggest?.h||0)/bounds.h,
    glyphCount:glyphs.length,glyphInkFraction:glyphs.reduce((s,c)=>s+c.area,0)/ink,regularGlyphs,
    envelopeUniformity,tallBinFraction,coreHeightFraction:median(spans)/bounds.h,verticalScale:bounds.h/textHeight};
}

/**
 * group: {x,y,w,h,members?:componentIds}; context: {mask,width,height,words?,
 * textHeight?,components?,inStamp?}. `inStamp` allows an inside-seal signature;
 * it never exempts short printed names from the ordinary text rejection.
 */
export function classifyStrokeGroup(group,context) {
  const f=groupFeatures(group,context);
  const verdict=(accepted,reason,confidence=0)=>({accepted,reason,confidence,features:{
    ink:f.ink,bounds:f.bounds,aspect:f.aspect,verticalScale:f.verticalScale,linearity:f.linearity,
    ocrCoverage:f.ocrCoverage,readableWords:f.readableWords,longestSpan:f.longestSpan,
    largestFraction:f.largestFraction,glyphCount:f.glyphCount,regularGlyphs:f.regularGlyphs,
    envelopeUniformity:f.envelopeUniformity,tallBinFraction:f.tallBinFraction,coreHeightFraction:f.coreHeightFraction,
  }});
  if(!f.ink)return verdict(false,'empty');
  const {w,h}=f.bounds;
  if(f.ink<36||w<Math.max(27,f.textHeight*1.5)||h<Math.max(12,f.textHeight*.7))return verdict(false,'too-small');
  if(f.linearity<.0025||h<5||f.density>.58)return verdict(false,'straight-rule');
  if(f.regularGlyphs&&f.glyphInkFraction>.60&&f.longestSpan<.57)return verdict(false,'regular-text');
  // Small text-sized remnants such as "ости.," must not be offered as objects,
  // including when OCR fails and gives no box for the fragment.
  const continuousFlourish=f.longestSpan>.70&&f.largestFraction>.50&&h>=Math.max(25,f.textHeight*1.55)&&w>h*1.2;
  if(f.verticalScale<1.45&&!continuousFlourish)return verdict(false,'printed-fragment');
  if(f.ocrCoverage>.65&&f.readableWords>0&&f.verticalScale<1.7&&!continuousFlourish)return verdict(false,'printed-fragment');
  const longBaseline=f.aspect>8.2&&f.verticalScale<4.8;
  const sentence=f.readableWords>=3&&f.aspect>4.4&&f.verticalScale<3.0;
  const uniformSentence=f.aspect>6.0&&f.envelopeUniformity>.76&&f.linearity<.025&&f.verticalScale<3.4;
  const ordinaryCursive=f.aspect>4.2&&f.linearity<.024&&f.coreHeightFraction<.50&&f.envelopeUniformity>.70&&f.verticalScale<4;
  if(longBaseline||sentence||uniformSentence||ordinaryCursive)return verdict(false,'running-handwriting');
  if(f.aspect<.8||f.aspect>14||f.longestSpan<.38||f.largestFraction<.24)return verdict(false,'uncertain');
  let confidence=.48+Math.min(.18,f.verticalScale*.045)+f.longestSpan*.13+f.largestFraction*.09;
  if(f.aspect>=1.6&&f.aspect<=6.8)confidence+=.05;
  if(f.ocrCoverage>.7&&!continuousFlourish)confidence-=.10;
  if(f.readableWords>=2)confidence-=.07;
  confidence=clamp(confidence,.01,.96);
  return verdict(confidence>=.71,confidence>=.71?'signature':'uncertain',confidence);
}

/** Evaluate groups together so small fragments of a long annotation row do
 * not become separate signatures just because the annotation has spaces. */
export function filterSignatureGroups(groups,context) {
  const components=context.components||strokeComponents(context.mask,context.width,context.height);
  const evaluated=groups.map(group=>({group,...classifyStrokeGroup(group,{...context,components})}));
  const runningRows=evaluated.filter(r=>r.reason==='running-handwriting').map(r=>r.features.bounds);
  // Spaces may split a sentence into three apparently compact components.
  // Compare their common baseline before deciding that each word is a signature.
  const rowCandidates=evaluated.filter(r=>r.features.bounds&&r.features.aspect>1.8&&r.features.verticalScale<4.2);
  const used=new Set();
  for(const first of rowCandidates) {
    if(used.has(first))continue;
    const members=[first];used.add(first);
    for(let index=0;index<members.length;index++)for(const next of rowCandidates) {
      if(used.has(next))continue;
      const a=members[index].features.bounds,b=next.features.bounds;
      const closeHeight=Math.max(a.h,b.h)<Math.min(a.h,b.h)*1.8;
      const baseline=Math.abs((a.y+a.h*.68)-(b.y+b.h*.68))<Math.max(a.h,b.h)*.42;
      const gap=Math.max(0,a.x-b.x-b.w,b.x-a.x-a.w);
      if(closeHeight&&baseline&&gap<Math.max(a.h,b.h)*1.7){members.push(next);used.add(next);}
    }
    if(members.length<2)continue;
    const left=Math.min(...members.map(r=>r.features.bounds.x)),right=Math.max(...members.map(r=>r.features.bounds.x+r.features.bounds.w));
    const top=Math.min(...members.map(r=>r.features.bounds.y)),bottom=Math.max(...members.map(r=>r.features.bounds.y+r.features.bounds.h));
    if((right-left)/(bottom-top)<8.1)continue;
    runningRows.push({x:left,y:top,w:right-left,h:bottom-top});
    for(const result of members){result.accepted=false;result.reason='running-handwriting';result.confidence=0;}
  }
  for(const result of evaluated) {
    if(!result.accepted)continue;
    const box=result.features.bounds;
    if(runningRows.some(row=>box.h<row.h*1.35&&Math.abs((box.y+box.h*.6)-(row.y+row.h*.6))<row.h*.38&&
      Math.max(0,row.x-box.x-box.w,box.x-row.x-row.w)<row.h*3)) {
      result.accepted=false;result.reason='annotation-row-fragment';result.confidence=0;
    }
  }
  return {accepted:evaluated.filter(r=>r.accepted),rejected:evaluated.filter(r=>!r.accepted)};
}

/** Remove proven running-handwriting components BEFORE neighboring components
 * are grouped into signatures. Otherwise a cursive sentence can enlarge a
 * signature's bounding box and inherit its confidence. Returned rows describe
 * observed ink; mixed components crossing those rows are reported as uncertain.
 * This function does not erase an arbitrary full rectangular text band.
 */
export function suppressAnnotationRows(mask,width,height,context={}) {
  const components=context.components||strokeComponents(mask,width,height);
  const candidates=components.boxes.filter(c=>c.area>=35&&c.w>=25&&c.h>=10).map(c=>({...c,members:[c.id]}));
  const classified=filterSignatureGroups(candidates,{...context,mask,width,height,components});
  const rowParts=classified.rejected.filter(r=>r.reason==='running-handwriting'||r.reason==='annotation-row-fragment');
  const removedIds=new Set(rowParts.map(r=>r.group.id)),rows=[];
  for(const result of rowParts) {
    const b=result.features.bounds,cy=b.y+b.h*.6;
    const row=rows.find(r=>Math.abs(r.cy-cy)<Math.max(r.h,b.h)*.4&&Math.max(r.h,b.h)<Math.min(r.h,b.h)*1.85);
    if(row){const right=Math.max(row.x+row.w,b.x+b.w),bottom=Math.max(row.y+row.h,b.y+b.h);row.x=Math.min(row.x,b.x);row.y=Math.min(row.y,b.y);row.w=right-row.x;row.h=bottom-row.y;row.cy=(row.cy+cy)/2;row.members.push(result.group.id);}
    else rows.push({...b,cy,members:[result.group.id]});
  }
  for(const row of rows) {
    // Follow actual ink support through spaces. This locates small terminal
    // words on the same line even when its middle word touches a signature.
    const occupied=new Uint8Array(width),top=Math.max(0,row.y),bottom=Math.min(height,row.y+row.h);
    for(let x=0;x<width;x++)for(let y=top;y<bottom;y++)if(mask[y*width+x]){occupied[x]=1;break;}
    const maxGap=Math.max(8,Math.round(row.h*.5));
    let left=row.x,right=row.x+row.w-1,gap=0;
    for(let x=left-1;x>=0;x--){if(occupied[x]){left=x;gap=0;}else if(++gap>maxGap)break;}
    gap=0;for(let x=right+1;x<width;x++){if(occupied[x]){right=x;gap=0;}else if(++gap>maxGap)break;}
    row.x=left;row.w=right-left+1;
    for(const c of components.boxes) {
      if(removedIds.has(c.id)||c.area<3||c.h>row.h*.88||c.w>row.h*2.4)continue;
      const cy=c.y+c.h*.6;
      if(Math.abs(cy-row.cy)>row.h*.4||c.x<row.x-row.h*.25||c.x+c.w>row.x+row.w+row.h*.25)continue;
      removedIds.add(c.id);row.members.push(c.id);
    }
  }
  const result=new Uint8Array(mask),removedMask=new Uint8Array(mask.length),uncertainMask=new Uint8Array(mask.length);
  const mixedIds=new Set(components.boxes.filter(c=>!removedIds.has(c.id)&&rows.some(row=>
    c.h>row.h*1.4&&overlap(c,row)>Math.min(area(c),area(row))*.1)).map(c=>c.id));
  let removedPixels=0,uncertainPixels=0;
  for(let i=0;i<mask.length;i++)if(mask[i]) {
    const id=components.labels[i];
    if(removedIds.has(id)){result[i]=0;removedMask[i]=1;removedPixels++;}
    else if(mixedIds.has(id)) {
      const x=i%width,y=Math.floor(i/width);
      if(rows.some(r=>x>=r.x&&x<r.x+r.w&&y>=r.y&&y<r.y+r.h)){uncertainMask[i]=1;uncertainPixels++;}
    }
  }
  let tracedPaths=0;
  if(context.traceMixed!==false&&mixedIds.size&&rows.length) {
    const traced=traceMixedAnnotationInk(result,width,height,rows);
    tracedPaths=traced.paths;
    for(let i=0;i<result.length;i++) {
      if(result[i]&&!traced.mask[i]){result[i]=0;removedMask[i]=1;removedPixels++;}
      if(uncertainMask[i]&&!result[i]){uncertainMask[i]=0;uncertainPixels--;}
    }
  }
  return {mask:result,removedMask,uncertainMask,rows,removedPixels,uncertainPixels,tracedPaths,removedComponentIds:[...removedIds]};
}

function skeletonNeighbors(index,skeleton,width,height) {
  const x=index%width,y=Math.floor(index/width),neighbors=[];
  for(let dy=-1;dy<=1;dy++)for(let dx=-1;dx<=1;dx++) {
    if(!dx&&!dy)continue;
    const xx=x+dx,yy=y+dy,n=yy*width+xx;
    if(xx<0||xx>=width||yy<0||yy>=height||!skeleton[n])continue;
    if(dx&&dy&&(skeleton[y*width+xx]||skeleton[yy*width+x]))continue;
    neighbors.push(n);
  }
  return neighbors;
}

/** Follow already visible stroke centerlines through a known annotation row.
 * Strong signature strokes outside that row provide entry directions. At a
 * crossing, the path with the smallest direction change is preferred. No new
 * pixels are synthesized: intersections remain explicitly uncertain. */
function traceMixedAnnotationInk(mask,width,height,rows) {
  const result=new Uint8Array(mask),skeleton=skeletonize(mask,width,height);let paths=0;
  for(const row of rows) {
    const inside=i=>{const x=i%width,y=Math.floor(i/width);return x>=row.x&&x<row.x+row.w&&y>=row.y&&y<row.y+row.h;};
    const outside=new Uint8Array(skeleton);
    for(let y=row.y;y<row.y+row.h;y++)for(let x=row.x;x<row.x+row.w;x++)outside[y*width+x]=0;
    const outer=strokeComponents(outside,width,height),anchors=new Set(outer.boxes.filter(c=>
      c.h>=Math.max(13,row.h*.36)&&c.area>=Math.max(18,row.h*.45)).map(c=>c.id));
    if(!anchors.size)continue;
    const entries=[];
    for(let y=row.y;y<row.y+row.h;y++)for(let x=row.x;x<row.x+row.w;x++) {
      const i=y*width+x;if(!skeleton[i])continue;
      for(const n of skeletonNeighbors(i,skeleton,width,height))if(!inside(n)&&anchors.has(outer.labels[n]))entries.push({index:i,outside:n});
    }
    const keepSkeleton=new Uint8Array(mask.length);let rowPaths=0;
    for(const entry of entries) {
      let current=entry.outside,previous=entry.index;const exterior=[entry.index,current];
      for(let step=0;step<10;step++) {
        const next=skeletonNeighbors(current,skeleton,width,height).filter(n=>n!==previous&&!inside(n)&&!exterior.includes(n));
        if(next.length!==1)break;
        previous=current;current=next[0];exterior.push(current);
      }
      if(exterior.length<4)continue;
      const startPath=exterior.reverse();
      const frontier=[{path:startPath,seen:new Set(startPath),current:entry.index,previous:entry.outside,cost:0}],deadEnds=[];
      let best=null;
      // Explore a small set of junction alternatives. Greedy continuation can
      // follow the crossing annotation into a loop and abandon the signature's
      // long diagonal, even when a compatible exit is still visibly present.
      for(let expansion=0;frontier.length&&expansion<180;expansion++) {
        frontier.sort((a,b)=>a.cost-b.cost);
        const state=frontier.shift();
        if(best&&state.cost>=best.cost)break;
        const {path,seen}=state;let {current,previous,cost}=state;
        for(let step=0;step<Math.min(1000,row.h*10);step++) {
          const reference=path[Math.max(0,path.length-7)],cx=current%width,cy=Math.floor(current/width);
          let dx=cx-reference%width,dy=cy-Math.floor(reference/width),length=Math.hypot(dx,dy);
          if(!length){dx=cx-previous%width;dy=cy-Math.floor(previous/width);length=Math.hypot(dx,dy)||1;}
          const choices=skeletonNeighbors(current,skeleton,width,height).filter(n=>n!==previous&&!seen.has(n)).map(n=>{
            const nx=n%width-cx,ny=Math.floor(n/width)-cy;
            return {index:n,alignment:(nx*dx+ny*dy)/(Math.hypot(nx,ny)*length)};
          }).filter(choice=>choice.alignment>=-.15).sort((a,b)=>b.alignment-a.alignment);
          if(!choices.length){if(path.length-startPath.length>=3&&path.length-startPath.length<row.h*3)deadEnds.push({path:[...path],cost});break;}
          if(choices.length>1) {
            for(const choice of choices.slice(0,3)) {
              const nextPath=[...path,choice.index],nextSeen=new Set(seen);nextSeen.add(choice.index);
              const nextCost=cost+1+Math.acos(clamp(choice.alignment,-1,1))**2*8;
              if(!inside(choice.index)) {
                if(anchors.has(outer.labels[choice.index])&&(!best||nextCost<best.cost))best={path:nextPath,cost:nextCost};
              } else frontier.push({path:nextPath,seen:nextSeen,current:choice.index,previous:current,cost:nextCost});
            }
            break;
          }
          const choice=choices[0],next=choice.index;
          cost+=1+Math.max(0,.25-choice.alignment)*2;
          previous=current;current=next;path.push(current);seen.add(current);
          if(!inside(current)) {
            if(anchors.has(outer.labels[current])&&(!best||cost<best.cost))best={path:[...path],cost};
            break;
          }
        }
      }
      if(!best&&deadEnds.length)best=deadEnds.sort((a,b)=>a.cost-b.cost)[0];
      if(!best)continue;
      for(const index of best.path)if(inside(index))keepSkeleton[index]=1;
      rowPaths++;
    }
    if(!rowPaths)continue;
    const corridors=new Uint8Array(mask.length),radius=2.35;
    for(let y=row.y;y<row.y+row.h;y++)for(let x=row.x;x<row.x+row.w;x++)if(keepSkeleton[y*width+x]) {
      for(let dy=-2;dy<=2;dy++)for(let dx=-2;dx<=2;dx++)if(dx*dx+dy*dy<=radius*radius) {
        const xx=x+dx,yy=y+dy;if(xx>=0&&xx<width&&yy>=0&&yy<height)corridors[yy*width+xx]=1;
      }
    }
    // Restrict removal to components with a real outside anchor. Detached ink
    // was already handled by the component classifier above.
    const full=strokeComponents(result,width,height),anchoredIds=new Set();
    for(let i=0;i<outside.length;i++)if(outside[i]&&anchors.has(outer.labels[i])&&full.labels[i])anchoredIds.add(full.labels[i]);
    for(let y=row.y;y<row.y+row.h;y++)for(let x=row.x;x<row.x+row.w;x++) {
      const i=y*width+x;if(result[i]&&anchoredIds.has(full.labels[i])&&!corridors[i])result[i]=0;
    }
    paths+=rowPaths;
  }
  return {mask:result,paths};
}

/** Keep the selected original component strokes and nearby detached accents.
 * Text/other handwriting elsewhere in the crop is excluded. This deliberately
 * cannot split two same-colour strokes which are physically connected.
 */
export function isolateSignatureMask(mask,width,height,group,context={}) {
  const components=context.components||strokeComponents(mask,width,height);
  const words=normalizedWords(context.words||context.textBoxes);
  let seedIds=group.members?.length?new Set(group.members):null;
  const local=components.boxes.filter(c=>overlap(c,group)>area(c)*.7);
  if(!seedIds) {
    const scored=local.map(c=>({component:c,...classifyStrokeGroup({...c,members:[c.id]},
      {...context,mask,width,height,components})})).filter(r=>r.accepted);
    scored.sort((a,b)=>b.component.area-a.component.area);
    seedIds=new Set(scored.length?[scored[0].component.id]:[]);
  }
  const seeds=components.boxes.filter(c=>seedIds.has(c.id));
  if(!seeds.length)return {mask:new Uint8Array(mask.length),keptPixels:0,removedPixels:mask.reduce((a,b)=>a+!!b,0),componentIds:[]};
  const left=Math.min(...seeds.map(c=>c.x)),top=Math.min(...seeds.map(c=>c.y));
  const right=Math.max(...seeds.map(c=>c.x+c.w)),bottom=Math.max(...seeds.map(c=>c.y+c.h));
  const seedBox={x:left,y:top,w:right-left,h:bottom-top},textHeight=Number(context.textHeight)||18;
  const margin=Math.max(4,Math.min(textHeight*.55,seedBox.h*.15));
  for(const c of local) {
    if(seedIds.has(c.id)||c.area<3)continue;
    const inWord=words.some(w=>w.confidence>=45&&overlap(w,c)>area(c)*.7&&
      c.w<Math.max(c.h*1.8,w.w*.55)&&c.h<w.h*1.4);
    if(inWord)continue;
    const dx=Math.max(0,seedBox.x-c.x-c.w,c.x-seedBox.x-seedBox.w);
    const dy=Math.max(0,seedBox.y-c.y-c.h,c.y-seedBox.y-seedBox.h);
    // Detached dots/short pen lifts inside the actual signature envelope; not
    // arbitrarily every word that happens to be in the rectangular export crop.
    if(dx<=margin&&dy<=margin&&c.w<seedBox.w*.32&&c.h<seedBox.h*.48&&c.area<seeds.reduce((sum,s)=>sum+s.area,0)*.2)seedIds.add(c.id);
  }
  const result=new Uint8Array(mask.length);let keptPixels=0,removedPixels=0;
  for(let i=0;i<mask.length;i++)if(mask[i]) {
    if(seedIds.has(components.labels[i])){result[i]=1;keptPixels++;}else removedPixels++;
  }
  return {mask:result,keptPixels,removedPixels,componentIds:[...seedIds]};
}
