# Draws Assets/app.ico: the splash's dark slate with "IME" in white. Needs Pillow and Inter
# (rsms.me/inter); pass the font path as the first argument if it isn't at FONT below.
# Run: python make-icon.py [InterDisplay-Bold.otf]
from PIL import Image, ImageDraw, ImageFont
BG=(0x1b,0x1b,0x22,255); EDGE=(0x3a,0x3a,0x48,255); TEXT=(255,255,255,255); MUTED=(0xa0,0xa0,0xb0,255)
import sys
FONT=sys.argv[1] if len(sys.argv)>1 else '/usr/share/fonts/opentype/inter/InterDisplay-Bold.otf'
def render(size):
    S=4; n=size*S
    im=Image.new('RGBA',(n,n),(0,0,0,0)); d=ImageDraw.Draw(im)
    r=round(n*0.2)
    d.rounded_rectangle([0,0,n-1,n-1],radius=r,fill=EDGE)          # thin edge so it shows on dark taskbars
    e=max(S, round(n*0.018))
    d.rounded_rectangle([e,e,n-1-e,n-1-e],radius=r-e,fill=BG)
    small=size<=24
    fs=n*(0.40 if small else 0.34)
    font=ImageFont.truetype(FONT,round(fs))
    text='IME'
    l,t,rr,b=d.textbbox((0,0),text,font=font)
    w,h=rr-l,b-t
    if w>n*0.82:
        font=ImageFont.truetype(FONT,round(fs*n*0.82/w)); l,t,rr,b=d.textbbox((0,0),text,font=font); w,h=rr-l,b-t
    bar=not small
    gap=n*0.07; bh=max(S,round(n*0.045)); bw=w*0.55
    total=h+(gap+bh if bar else 0)
    y=(n-total)/2 - t
    d.text(((n-w)/2-l,y),text,font=font,fill=TEXT)
    if bar:
        by=(n-total)/2+h+gap
        d.rounded_rectangle([(n-bw)/2,by,(n+bw)/2,by+bh],radius=bh/2,fill=MUTED)
    return im.resize((size,size),Image.LANCZOS)
sizes=[16,20,24,32,40,48,64,128,256]
ims={s:render(s) for s in sizes}
import os
ims[256].save(os.path.join(os.path.dirname(os.path.abspath(__file__)),'app.ico'),sizes=[(s,s) for s in sizes],append_images=[ims[s] for s in sizes if s!=256])
