import { useCallback, useEffect, useRef } from 'react';

const FADE_PX = 24;

/**
 * Drives the `scroll-edge-fade` mask of a scroll container. A side only fades while
 * content is cut off there: top when scrollTop > 0, bottom when scrollTop + clientHeight
 * is short of scrollHeight. Recomputed on scroll and whenever the container or its
 * content changes size. Writes CSS variables directly, so scrolling never re-renders.
 */
export function useScrollEdgeFade<T extends HTMLElement>() {
  const nodeRef = useRef<T | null>(null);
  const cleanupRef = useRef<(() => void) | null>(null);

  const ref = useCallback((node: T | null) => {
    cleanupRef.current?.();
    cleanupRef.current = null;
    nodeRef.current = node;
    if (!node) return;

    const update = () => {
      const { scrollTop, clientHeight, scrollHeight } = node;
      const top = scrollTop > 1 ? FADE_PX : 0;
      const bottom = scrollTop + clientHeight < scrollHeight - 1 ? FADE_PX : 0;
      node.style.setProperty('--scroll-fade-top', `${top}px`);
      node.style.setProperty('--scroll-fade-bottom', `${bottom}px`);
    };

    update();
    node.addEventListener('scroll', update, { passive: true });
    const observer = new ResizeObserver(update);
    observer.observe(node);
    const observeChildren = () => Array.from(node.children).forEach((child) => observer.observe(child));
    observeChildren();
    // Sections can appear later (roles load), so re-observe when the child list changes.
    const mutations = new MutationObserver(() => {
      observeChildren();
      update();
    });
    mutations.observe(node, { childList: true });
    cleanupRef.current = () => {
      node.removeEventListener('scroll', update);
      observer.disconnect();
      mutations.disconnect();
    };
  }, []);

  useEffect(() => () => cleanupRef.current?.(), []);

  return ref;
}
