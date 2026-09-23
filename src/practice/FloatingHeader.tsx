import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';

export function FloatingHeader({ children, open, setOpen }: { children: ReactNode; open: boolean; setOpen: (value: boolean) => void }) {
  const slot = useRef<HTMLDivElement>(null);
  const shell = useRef<HTMLDivElement>(null);
  const waitForPointerExit = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const [hidden, setHidden] = useState(false);
  const clear = () => { clearTimeout(timer.current); };
  const reveal = () => { clear(); setOpen(true); };
  const scheduleClose = () => {
    clear();
    timer.current = setTimeout(() => {
      if (!shell.current?.matches(':hover, :focus-within')) setOpen(false);
    }, 120);
  };
  useLayoutEffect(() => {
    const host = slot.current!;
    const header = host.querySelector<HTMLElement>('.masthead')!;
    const scroller = host.closest('.practice-scroll')!;
    const check = () => {
      const isHidden = host.getBoundingClientRect().bottom <= 1;
      setHidden(isHidden);
      if (scroller.scrollTop <= 1) setOpen(false);
    };
    const size = () => { host.style.height = `${header.offsetHeight}px`; host.style.setProperty('--header-height', `${header.offsetHeight}px`); host.closest('main')?.style.setProperty('--floating-header-height', `${header.offsetHeight}px`); check(); };
    const observer = new ResizeObserver(size);
    observer.observe(header);
    scroller.addEventListener('scroll', check, { passive: true });
    size();
    return () => { observer.disconnect(); scroller.removeEventListener('scroll', check); };
  }, [setOpen]);
  useEffect(() => () => clearTimeout(timer.current), []);
  return <div className="header-slot" ref={slot}>
    {hidden && !open && <button className="top-reveal-zone" aria-label="제어 메뉴 열기" title="제어 메뉴 열기"
      onPointerEnter={() => { if (!waitForPointerExit.current) reveal(); }}
      onPointerLeave={() => { clear(); waitForPointerExit.current = false; }} onFocus={() => {
        reveal();
        requestAnimationFrame(() => shell.current?.querySelector<HTMLElement>('select, button')?.focus({ preventScroll: true }));
      }} onClick={reveal}><span aria-hidden="true" /></button>}
    <div ref={shell} className={`header-shell${hidden && open ? ' is-floating' : ''}`}
      onPointerEnter={clear} onPointerLeave={scheduleClose}
      onFocus={() => { clear(); if (hidden) setOpen(true); }} onBlur={scheduleClose}
      onKeyDown={event => {
        if (event.key !== 'Escape' || !open) return;
        event.stopPropagation(); waitForPointerExit.current = true; setOpen(false);
        slot.current?.closest('main')?.querySelector<HTMLElement>('.practice-surface')?.focus({ preventScroll: true });
      }}>
      {children}
    </div>
  </div>;
}
