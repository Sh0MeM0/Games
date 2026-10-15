import test from 'node:test';
import assert from 'node:assert/strict';
import {mkdtemp,readFile,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import path from 'node:path';
import exporter from '../lib/export.cjs';
const {safeName,writeUniquePng}=exporter;
// Valid 1x1 opaque white PNG.
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADUlEQVQIHWP4//8/AAX+Av7czFnnAAAAAElFTkSuQmCC','base64');
test('export names cannot traverse directories or use Windows device names',()=>{
  for(const name of ['../../secret','C:\\file','a/b','a\u0000b','CON','PRN.txt','NUL.png','LPT1']){
    const sanitized=safeName(name);assert.ok(!/[\\/\x00]/.test(sanitized));assert.ok(!/^(CON|PRN|AUX|NUL|LPT1)(\.|$)/i.test(sanitized));
  }
  assert.equal(safeName('Печать 1.png'),'Печать 1');
});
test('two exports with the same name never overwrite an existing file',async()=>{
  const dir=await mkdtemp(path.join(tmpdir(),'stamp-test-'));
  try{
    const first=await writeUniquePng(dir,'Печать',png),second=await writeUniquePng(dir,'Печать',png);
    assert.equal(first,'Печать.png');assert.equal(second,'Печать_2.png');assert.deepEqual(await readFile(path.join(dir,first)),png);
    await assert.rejects(writeUniquePng(dir,'bad',Buffer.from('not a PNG')),/Некорректный/);
  }finally{await rm(dir,{recursive:true,force:true});}
});
