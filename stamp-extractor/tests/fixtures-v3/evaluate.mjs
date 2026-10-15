/** Report difficult authored fixtures honestly, including remaining failures.
 * Run from any working directory: node tests/fixtures-v3/evaluate.mjs
 */
import {readFileSync,writeFileSync,mkdirSync} from 'node:fs';
import {fileURLToPath} from 'node:url';
import path from 'node:path';
import {PNG} from 'pngjs';
import {analyzeDocument,renderExtractedRegion} from '../../lib/extraction.js';
import ocrModule from '../../lib/ocr.cjs';

const here=path.dirname(fileURLToPath(import.meta.url)),root=path.resolve(here,'../..');
const useOcr=process.argv.includes('--ocr'),ocr=useOcr?new ocrModule.OfflineOcr():null;
const output=path.join(root,'test-results/failures-v3',useOcr?'ocr':'known-words');mkdirSync(output,{recursive:true});
const names=['failures-rectangular','failures-rectangular-clean','failures-form-signature','failures-text-only','failures-text-only-blue'];
const read=file=>PNG.sync.read(readFileSync(file));
const ink=(image,index)=>image.data[index*4]>32;
const box=o=>({x:o.bbox.x,y:o.bbox.y,w:o.bbox.width,h:o.bbox.height});
const overlap=(a,b)=>Math.max(0,Math.min(a.x+a.w,b.x+b.w)-Math.max(a.x,b.x))*Math.max(0,Math.min(a.y+a.h,b.y+b.h)-Math.max(a.y,b.y));
const dark=(image,index)=>Math.min(image.data[index*4],image.data[index*4+1],image.data[index*4+2])<230;
const ratio=(a,b)=>b?Math.round(a/b*10000)/10000:null;
const reports=[];
try {
for(const name of names) {
  const truth=JSON.parse(readFileSync(path.join(here,name+'.json'))),source=read(path.join(root,'examples',name+'.png'));
  // Deliberately omit generator-only role metadata from algorithm inputs.
  const words=useOcr?(await ocr.recognize(readFileSync(path.join(root,'examples',name+'.png')))).words:truth.printed_words.map(({text,confidence,bbox})=>({text,confidence,bbox}));
  const analysis=analyzeDocument(source,words),regions=analysis.regions;
  const report={name,wordSource:useOcr?'bundled-offline-ocr':'generator',wordCount:words.length,expected:truth.expected_counts,detected:{stamp:regions.filter(r=>r.kind==='stamp').length,signature:regions.filter(r=>r.kind==='signature').length},objects:[],regions:regions.map(({selection,...region})=>({...region,selection:selection?{x:selection.x,y:selection.y,w:selection.w,h:selection.h}:undefined}))};
  report.countsMatch=report.expected.stamp===report.detected.stamp&&report.expected.signature===report.detected.signature;
  for(const object of truth.objects) {
    const target=box(object),candidates=regions.filter(r=>r.kind===object.kind).sort((a,b)=>overlap(b,target)/(b.w*b.h)-overlap(a,target)/(a.w*a.h));
    const region=candidates.find(r=>overlap(r,target)>target.w*target.h*.35);
    if(!region){report.objects.push({id:object.id,detected:false});continue;}
    const extracted=renderExtractedRegion(source,region,region,[],analysis),visible=read(path.join(here,object.visible_mask)),full=read(path.join(here,object.mask));
    const p=new PNG({width:extracted.width,height:extracted.height});p.data=Buffer.from(extracted.data);writeFileSync(path.join(output,name+'.'+object.id+'.png'),PNG.sync.write(p));
    const letter=object.letter_mask?read(path.join(here,object.letter_mask)):null;
    const backgrounds=truth.backgrounds.map(b=>({...b,image:read(path.join(here,b.mask))}));
    const unwanted=Object.fromEntries(backgrounds.map(b=>[b.id,{retained:0,total:0}]));
    let kept=0,total=0,letterKept=0,letterTotal=0;
    for(let y=0;y<source.height;y++)for(let x=0;x<source.width;x++) {
      const i=y*source.width+x,inside=x>=region.x&&x<region.x+extracted.width&&y>=region.y&&y<region.y+extracted.height;
      const outputInk=inside&&dark(extracted,(y-region.y)*extracted.width+x-region.x);
      if(ink(visible,i)){total++;if(outputInk)kept++;if(letter&&ink(letter,i)){letterTotal++;if(outputInk)letterKept++;}}
      if(!inside||ink(full,i))continue;
      // Exclude one-pixel antialias margins from foreign ink measurements.
      let nextToDesired=false;
      for(let yy=Math.max(0,y-1);yy<=Math.min(source.height-1,y+1)&&!nextToDesired;yy++)for(let xx=Math.max(0,x-1);xx<=Math.min(source.width-1,x+1);xx++)if(ink(full,yy*source.width+xx)){nextToDesired=true;break;}
      if(nextToDesired)continue;
      for(const background of backgrounds)if(ink(background.image,i)){const stats=unwanted[background.id];stats.total++;if(outputInk)stats.retained++;}
    }
    for(const value of Object.values(unwanted))value.retainedFraction=ratio(value.retained,value.total);
    report.objects.push({id:object.id,detected:true,visibleInkRetained:ratio(kept,total),visibleLetteringRetained:ratio(letterKept,letterTotal),unwanted});
  }
  reports.push(report);
  console.log(JSON.stringify(report));
}
writeFileSync(path.join(output,'report.json'),JSON.stringify(reports,null,2)+'\n');
} finally {ocr?.close();}
