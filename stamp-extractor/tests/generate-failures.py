#!/usr/bin/env python3
"""Authored regression documents reproducing reported failure *layouts*.

No user attachment is copied or reconstructed here. Every pen path and stamp is
newly authored test data. Run with Pillow, NumPy and system DejaVu fonts.
"""
from __future__ import annotations

import json
import math
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont, ImageOps

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'tests' / 'fixtures-v3'
EXAMPLES = ROOT / 'examples'
SCALE = 3
BLUE = (26, 34, 135)
PALE_BLUE = (71, 94, 164)
BLACK = (25, 25, 27)
FONTS = Path('/usr/share/fonts/truetype/dejavu')


def layer(size):
    return Image.new('RGBA', tuple(v*SCALE for v in size), (255,255,255,0))


def font(size, bold=False, serif=False):
    family = 'DejaVuSerif' if serif else 'DejaVuSans'
    return ImageFont.truetype(str(FONTS/(family+('-Bold' if bold else '')+'.ttf')), round(size*SCALE))


def down(image, size):
    return image.resize(size, Image.Resampling.LANCZOS)


def rule(image, points, color=BLACK, width=1.4):
    ImageDraw.Draw(image).line([(round(x*SCALE),round(y*SCALE)) for x,y in points], fill=(*color,255),width=round(width*SCALE))


def pen(image, points, color=BLUE, width=2.05, phase=0):
    p=np.asarray(points,dtype=float)
    t=np.linspace(0,1, max(55,round(np.linalg.norm(p[-1]-p[0])*2)))
    values=(1-t[:,None])**3*p[0]+3*(1-t[:,None])**2*t[:,None]*p[1]+3*(1-t[:,None])*t[:,None]**2*p[2]+t[:,None]**3*p[3]
    d=ImageDraw.Draw(image)
    for i in range(len(values)-1):
        pressure=.86+.19*math.sin(t[i]*math.pi*2+phase)
        d.line([tuple(values[i]*SCALE),tuple(values[i+1]*SCALE)],fill=(*color,251),width=max(2,round(width*pressure*SCALE)))


def text(image, value, x, y, size, words, role='background', color=BLACK,bold=False,serif=False):
    d=ImageDraw.Draw(image);f=font(size,bold,serif)
    # Coordinates and word boxes share a baseline, including descenders.
    d.text((x*SCALE,y*SCALE),value,font=f,fill=(*color,255),anchor='ls')
    prefix=''
    for word in value.split(' '):
        xx=x*SCALE+f.getlength(prefix)
        b=d.textbbox((xx,y*SCALE),word,font=f,anchor='ls')
        symbols=[]
        for i,char in enumerate(word):
            bb=d.textbbox((xx+f.getlength(word[:i]),y*SCALE),char,font=f,anchor='ls')
            symbols.append({'text':char,'confidence':99,'bbox':dict(zip(['x0','y0','x1','y1'],[round(v/SCALE) for v in bb]))})
        words.append({'text':word,'confidence':99,'role':role,'bbox':dict(zip(['x0','y0','x1','y1'],[round(v/SCALE) for v in b])),'symbols':symbols})
        prefix+=word+' '


def stamp(size, origin, angle, second=False):
    """Local rotated stamp, with separately measurable lettering and border."""
    w,h=(580,207) if second else (580,207)
    local=layer((w,h));letters=layer((w,h));frame=layer((w,h));words=[]
    color=PALE_BLUE if second else BLUE
    ImageDraw.Draw(frame).rectangle((3*SCALE,3*SCALE,(w-4)*SCALE,(h-4)*SCALE),outline=(*color,247),width=round(3.1*SCALE))
    rows=[('Проект СОГЛАСОВАН',41,24,True),('с филиалом «Учебный участок»',77,22,False),('УП «Примерводосеть»',111,24,False)] if second else [('Проект СОГЛАСОВАТЬ',41,28,True),('дополнительно с ПУ «Примергаз»',77,23,False),('УП «Примероблгаз»',111,24,False)]
    for value,y,sz,bold in rows:
        x=(w-font(sz,bold).getlength(value)/SCALE)/2
        text(letters,value,x,y,sz,words,'stamp',color,bold)
    text(letters,'Гл. инженер',18,183,25,words,'stamp',color)
    text(letters,'В. М. Тестов' if second else 'С. П. Образцов',357,183,24,words,'stamp',color)
    local.alpha_composite(frame);local.alpha_composite(letters)
    rotated=local.rotate(angle,Image.Resampling.BICUBIC,expand=True)
    out=layer(size);out.alpha_composite(rotated,tuple(round(v*SCALE) for v in origin))
    out_letters=layer(size);out_letters.alpha_composite(letters.rotate(angle,Image.Resampling.BICUBIC,expand=True),tuple(round(v*SCALE) for v in origin))
    out_frame=layer(size);out_frame.alpha_composite(frame.rotate(angle,Image.Resampling.BICUBIC,expand=True),tuple(round(v*SCALE) for v in origin))
    # Pillow rotates anticlockwise in screen coordinates around the image center.
    radians=math.radians(angle);co=math.cos(radians);si=math.sin(radians)
    def project(x,y):
        return (origin[0]+rotated.width/SCALE/2+co*(x-w/2)+si*(y-h/2),origin[1]+rotated.height/SCALE/2-si*(x-w/2)+co*(y-h/2))
    for word in words:
        for item in [word,*word['symbols']]:
            b=item['bbox'];points=[project(x,y) for x in [b['x0'],b['x1']] for y in [b['y0'],b['y1']]]
            item['bbox']={'x0':math.floor(min(p[0] for p in points)),'y0':math.floor(min(p[1] for p in points)),'x1':math.ceil(max(p[0] for p in points)),'y1':math.ceil(max(p[1] for p in points))}
    return out,out_letters,out_frame,words


def signature(size, origin, factor=1, complex=False):
    image=layer(size)
    # Long sweeping loops cross each other and black rules; deliberately unlike
    # disconnected neat zig-zag strokes in earlier tests.
    paths=[[(0,141),(98,0),(275,-48),(295,-12)],[(295,-12),(333,10),(130,96),(88,121)],[(88,121),(205,31),(314,20),(183,77)],[(183,77),(47,145),(-20,186),(0,141)],[(41,134),(139,53),(173,79),(111,123)],[(111,123),(81,149),(165,70),(158,101)],[(158,101),(145,127),(110,168),(104,150)],[(104,150),(88,129),(165,85),(173,93)],[(173,93),(179,102),(128,123),(128,138)],[(128,138),(130,168),(158,104),(155,136)],[(155,136),(146,169),(162,158),(170,137)],[(170,137),(179,113),(181,153),(183,128)],[(58,149),(161,106),(282,93),(389,109)]]
    if complex:
        paths += [[(52,139),(179,64),(248,93),(115,132)],[(115,132),(283,63),(333,64),(187,123)],[(187,123),(337,80),(264,124),(133,132)],[(133,132),(223,100),(287,161),(364,170)],[(364,170),(254,120),(130,94),(49,161)],[(49,161),(-57,226),(-10,238),(139,123)]]
    for i,p in enumerate(paths):
        points=[(origin[0]+x*factor,origin[1]+y*factor) for x,y in p]
        pen(image,points,width=2.15*factor,phase=.73*i)
    return image


def handwriting(size):
    image=layer(size)
    # Two long body-text-like cursive runs, with visible word breaks, crossing
    # both borders of the lower approval stamp and some of its own lettering.
    for row,(start,baseline,count) in enumerate([(-18,370,42),(-25,529,24)]):
        x=start
        for i in range(count):
            width=17+(i*7%11);rise=10+(i*13%9);y=baseline+math.sin(i*.9)*2.2
            if i in [10,20,31]:x+=17
            pen(image,[(x,y),(x+15,y-rise),(x+width+9,y-rise-7),(x+width-5,y-2)],width=2,phase=i)
            pen(image,[(x+width-5,y-2),(x+width-15,y+14),(x+width+4,y+3),(x+width+9,y-4)],width=2,phase=i*.7)
            if i in [2,15,28]:pen(image,[(x+3,y-2),(x-3,y-48),(x+23,y-37),(x+5,y+3)],width=1.9)
            if i in [6,17,34]:pen(image,[(x+width-5,y),(x+width-24,y+45),(x+width+18,y+23),(x+width+10,y-8)],width=1.9)
            x+=width+5
    return image


def bbox(mask):
    b=mask.point(lambda v:255 if v>32 else 0).getbbox()
    return {'x':b[0],'y':b[1],'width':b[2]-b[0],'height':b[3]-b[1]}


def save_document(name,size,objects,backgrounds,words,notes,order=None):
    paper=Image.new('RGBA',tuple(v*SCALE for v in size),'white')
    layers={**dict(backgrounds),**{obj['id']:obj['layer'] for obj in objects}}
    ordered=order or list(layers)
    for key in ordered:paper.alpha_composite(layers[key])
    # Mild paper mottling, using only luminance so class colors stay stable.
    clean=down(paper.convert('RGB'),size)
    rng=np.random.default_rng(9813);yy,xx=np.mgrid[:size[1],:size[0]]
    shade=1.0-.016*np.sin(xx/75)**2-.012*np.cos(yy/39)**2
    values=np.asarray(clean).astype(float)*shade[:,:,None]+rng.normal(0,.3,(*size[::-1],1))
    Image.fromarray(np.clip(values,0,255).astype('uint8')).save(EXAMPLES/f'{name}.png')
    truth={'authored':True,'description':notes,'width':size[0],'height':size[1],'expected_counts':{kind:sum(o['kind']==kind for o in objects) for kind in ['stamp','signature']},'printed_words':words,'objects':[],'backgrounds':[],'occlusion_note':'Full masks are generator references only. Same-color overlapping pixels cannot be uniquely separated from the flattened input. Visible masks exclude later opaque layers; do not use full masks as evidence of observed recoverable ink.'}
    for key,background in backgrounds:
        mask=down(background.getchannel('A'),size);filename=f'{name}.{key}.mask.png';mask.save(OUT/filename)
        truth['backgrounds'].append({'id':key,'mask':filename,'ink_pixels':int((np.asarray(mask)>32).sum())})
    for obj in objects:
        mask=down(obj['layer'].getchannel('A'),size);filename=f"{name}.{obj['id']}.mask.png";mask.save(OUT/filename)
        visible=np.asarray(mask).copy()
        for later in ordered[ordered.index(obj['id'])+1:]:
            blocker=np.asarray(down(layers[later].getchannel('A'),size));visible[blocker>128]=0
        vfilename=f"{name}.{obj['id']}.visible.mask.png";Image.fromarray(visible).save(OUT/vfilename)
        white=Image.new('RGBA',tuple(v*SCALE for v in size),'white');white.alpha_composite(obj['layer']);b=bbox(mask)
        reference=f"{name}.{obj['id']}.expected.png"
        down(white.convert('RGB'),size).crop((max(0,b['x']-16),max(0,b['y']-16),min(size[0],b['x']+b['width']+16),min(size[1],b['y']+b['height']+16))).save(OUT/reference)
        entry={k:v for k,v in obj.items() if k not in ['layer','letter_layer','frame_layer']};entry.update(bbox=b,mask=filename,visible_mask=vfilename,clean_reference=reference,ink_pixels=int((np.asarray(mask)>32).sum()))
        for part in ['letter','frame']:
            if part+'_layer' in obj:
                fn=f"{name}.{obj['id']}.{part}.mask.png";down(obj[part+'_layer'].getchannel('A'),size).save(OUT/fn);entry[part+'_mask']=fn
        truth['objects'].append(entry)
    (OUT/f'{name}.json').write_text(json.dumps(truth,ensure_ascii=False,indent=2)+'\n')


def main():
    OUT.mkdir(parents=True,exist_ok=True);EXAMPLES.mkdir(parents=True,exist_ok=True)
    size=(900,720);printed=layer(size);rules=layer(size);words=[]
    for value,x,y in [('комиссии, второй направляется',-31,144),('при предварительном',-20,174),('при необходимости —',-16,204),('районного исполнительного комитета / города',-72,273),('образец учебной страницы: 1 л.',-20,694)]:
        text(printed,value,x,y,21,words,serif=True)
    for y,end in [(19,404),(63,398),(252,404),(492,453)]:rule(rules,[(0,y),(end,y)],width=1.7)
    stamp1,letters1,frame1,own1=stamp(size,(222,81),3.8)
    stamp2,letters2,frame2,own2=stamp(size,(240,331),1.4,True)
    sig1=signature(size,(330,245),.65)
    sig2=signature(size,(356,468),.86,True)
    objects=[{'id':'approval-top','kind':'stamp','color':'blue','layer':stamp1,'letter_layer':letters1,'frame_layer':frame1}, {'id':'approval-bottom','kind':'stamp','color':'blue','layer':stamp2,'letter_layer':letters2,'frame_layer':frame2}, {'id':'signature-top','kind':'signature','color':'blue','layer':sig1},{'id':'signature-bottom','kind':'signature','color':'blue','layer':sig2}]
    words+=own1+own2
    save_document('failures-rectangular',size,objects,[('printed-text',printed),('form-rules',rules),('handwritten-body',handwriting(size))],words,'Authored layout stress test: two tilted rectangular approval stamps, two overlapping signatures and long unwanted same-color handwriting crossing the second frame. All names are fictional.',order=['printed-text','form-rules','approval-top','approval-bottom','signature-top','signature-bottom','handwritten-body'])
    save_document('failures-rectangular-clean',size,objects[:2],[('printed-text',printed),('form-rules',rules)],words,'Control: same two rectangular stamps and text, no handwriting or signatures.')

    size=(760,430);printed=layer(size);rules=layer(size);words=[]
    text(printed,'(подпись)',184,259,18,words,serif=True);text(printed,'(подпись)',186,369,18,words,serif=True)
    for a,b in [((79,239),(416,239)),((490,243),(759,243)),((79,349),(416,349)),((490,354),(759,354))]:rule(rules,[a,b],width=1.7)
    sig=signature(size,(46,151),1.7)
    objects=[{'id':'signature-loop','kind':'signature','color':'blue','layer':sig}]
    save_document('failures-form-signature',size,objects,[('printed-text',printed),('form-rules',rules)],words,'Authored long-loop signature crossed by black form rules and nearby parenthesized printed labels.',order=['signature-loop','form-rules','printed-text'])

    size=(760,430)
    for name,color in [('failures-text-only',BLACK),('failures-text-only-blue',BLUE)]:
        image=layer(size);words=[]
        for value,x,y,sz in [('ости.,',355,92,25),('С. П. Образцов',91,164,25),('филиалом',442,209,23),('«Учебный участок»',99,294,24),('12345',488,346,21),('Проект',325,397,26)]:
            text(image,value,x,y,sz,words,color=color,serif=True)
        save_document(name,size,[],[('printed-text',image)],words,'Negative fixture: scattered printed fragments, abbreviations, punctuation and numeric text; zero signatures and zero stamps.')


if __name__=='__main__':main()
