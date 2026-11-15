# Draws Assets/app.ico and app.png (the 256 px image, shipped for Linux .desktop files): the default Dark
# theme's greys with "IME" in white and a sprite-axis mark under it (blue line, white cross).
# Needs Pillow and Inter (rsms.me/inter); pass the font path as the first argument if it isn't at FONT below.
# Run: python make-icon.py [InterDisplay-Bold.otf]
import os, sys
from PIL import Image, ImageDraw, ImageFont
# Default Dark theme colors (Themes.Fallback): Canvas, Separator, Text, Dim, Accent
BG=(0x2b,0x2d,0x31,255); EDGE=(0x4f,0x54,0x5c,255); TEXT=(0xf2,0xf3,0xf5,255); ACCENT=(0x16,0x8f,0xe8,255)
FONT=sys.argv[1] if len(sys.argv)>1 else '/usr/share/fonts/opentype/inter/InterDisplay-Bold.otf'
S=4  # supersampling

def layout(n,size,track=0.0):
    font=ImageFont.truetype(FONT,round(n*(0.40 if size<=24 else 0.34)))
    def measure(f):
        xs=[]; x=0
        for c in 'IME':
            xs.append(x); x+=f.getlength(c)+f.size*track
        l=min(xs[i]+f.getbbox(c)[0] for i,c in enumerate('IME'))
        r=max(xs[i]+f.getbbox(c)[2] for i,c in enumerate('IME'))
        t=min(f.getbbox(c)[1] for c in 'IME'); b=max(f.getbbox(c)[3] for c in 'IME')
        return xs,l,t,r-l,b-t
    xs,l,t,w,h=measure(font)
    if w>n*0.82:
        font=ImageFont.truetype(FONT,round(font.size*n*0.82/w)); xs,l,t,w,h=measure(font)
    return font,xs,l,t,w,h

def draw_text(d,n,font,xs,l,t,w,top):
    x0=(n-w)/2-l
    for i,c in enumerate('IME'): d.text((x0+xs[i],top-t),c,font=font,fill=TEXT)

def block_top(n,h,extra,gap,optical):   # top of the text+mark group, centered
    return (n-(h+extra+gap))/2-n*optical

def render(size):
    n=size*S; small=size<=24
    im=Image.new('RGBA',(n,n),(0,0,0,0)); d=ImageDraw.Draw(im)
    r=round(n*0.2); e=max(S,round(n*0.018))
    d.rounded_rectangle([0,0,n-1,n-1],radius=r,fill=EDGE)      # thin edge so it shows on dark taskbars
    d.rounded_rectangle([e,e,n-1-e,n-1-e],radius=r-e,fill=BG)
    if small:
        # One-pixel line (plus a tiny cross from 20 px) snapped to whole pixels so it stays crisp
        font,xs,l,t,w,h=layout(n,size)
        gap=round(1.6*S); lh=S; arm=round(1.5*S) if size>=20 else 0
        ext=arm*2+lh if arm else lh
        top=round(block_top(n,h,ext,gap,0)/S)*S
        draw_text(d,n,font,xs,l,t,w,top)
        cy=round((top+h+gap+ext/2)/S)*S; cx=n/2; bw=round(w*0.62/S)*S
        d.rectangle([(n-bw)/2,cy-lh/2,(n+bw)/2,cy+lh/2],fill=ACCENT)
        if arm: d.rectangle([cx-lh/2,cy-arm,cx+lh/2,cy+arm],fill=TEXT)
    else:
        font,xs,l,t,w,h=layout(n,size,track=-0.01)
        gap=n*0.075; lh=max(S,round(n*0.024)); arm=n*0.058; bw=w*0.62; tw=max(S,round(n*0.032))
        top=block_top(n,h,arm*2,gap,0.015)
        draw_text(d,n,font,xs,l,t,w,top)
        cy=top+h+gap+arm; cx=n/2
        d.rectangle([(n-bw)/2,cy-lh/2,(n+bw)/2,cy+lh/2],fill=ACCENT)
        d.rectangle([cx-tw/2,cy-arm,cx+tw/2,cy+arm],fill=TEXT)
        d.rectangle([cx-arm,cy-tw/2,cx+arm,cy+tw/2],fill=TEXT)
    return im.resize((size,size),Image.LANCZOS)

sizes=[16,20,24,32,40,48,64,128,256]
ims={s:render(s) for s in sizes}
here=os.path.dirname(os.path.abspath(__file__))
ims[256].save(os.path.join(here,'app.ico'),sizes=[(s,s) for s in sizes],append_images=[ims[s] for s in sizes if s!=256])
ims[256].save(os.path.join(here,'app.png'))
