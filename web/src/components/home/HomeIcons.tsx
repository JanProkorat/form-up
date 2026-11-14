import type { ReactNode, SVGProps } from 'react';

interface IconProps {
  size?: number;
  strokeWidth?: number;
  children: ReactNode;
}

function Icon({ size = 18, strokeWidth = 1.8, children }: IconProps) {
  const props: SVGProps<SVGSVGElement> = {
    width: size,
    height: size,
    viewBox: '0 0 24 24',
    fill: 'none',
    stroke: 'currentColor',
    strokeWidth,
    strokeLinecap: 'round',
    strokeLinejoin: 'round',
    'aria-hidden': true,
  };
  return <svg {...props}>{children}</svg>;
}

interface SizedProps {
  size?: number;
  strokeWidth?: number;
}

export function IconCheck({ size = 12, strokeWidth = 2.6 }: SizedProps) {
  return (
    <Icon size={size} strokeWidth={strokeWidth}>
      <path d="M5 12.5l4.5 4.5L19 7.5" />
    </Icon>
  );
}

export function IconPlay({ size = 14 }: SizedProps) {
  return (
    <Icon size={size}>
      <path d="M6 4l13 8-13 8z" />
    </Icon>
  );
}

export function IconPulse({ size = 18 }: SizedProps) {
  return (
    <Icon size={size}>
      <path d="M3 12h4l3-7 4 14 3-7h4" />
    </Icon>
  );
}

export function IconSearch({ size = 18 }: SizedProps) {
  return (
    <Icon size={size}>
      <circle cx="11" cy="11" r="6.5" />
      <path d="M20 20l-4.2-4.2" />
    </Icon>
  );
}

export function IconApple({ size = 16 }: SizedProps) {
  return (
    <Icon size={size}>
      <path d="M15.5 4c-.3 1.6-1.6 2.9-3.1 2.8.1-1.5 1.5-2.8 3.1-2.8z" />
      <path d="M17.5 12.8c0-2 1.6-3 1.7-3.1-1-1.4-2.4-1.6-2.9-1.6-1.3-.1-2.4.7-3 .7-.6 0-1.6-.7-2.6-.7-1.4 0-2.6.8-3.3 2-1.4 2.4-.4 6 1 8 .7 1 1.4 2 2.4 2 1 0 1.3-.6 2.5-.6s1.5.6 2.5.6c1 0 1.7-1 2.3-2 .7-1 1-2 1-2.1 0 0-1.6-.6-1.6-3.2z" />
    </Icon>
  );
}

export function IconVideo({ size = 18 }: SizedProps) {
  return (
    <Icon size={size}>
      <rect x="3" y="5" width="18" height="14" rx="3" />
      <path d="M10 9.5v5l4.5-2.5z" />
    </Icon>
  );
}

/** Two rows and a column crossing: "build by dragging". */
export function IconHash({ size = 18 }: SizedProps) {
  return (
    <Icon size={size}>
      <path d="M5 9h14M5 15h14M9 5v14" />
    </Icon>
  );
}

/** Left-aligned lines of different lengths: "macros at a glance". */
export function IconLines({ size = 18 }: SizedProps) {
  return (
    <Icon size={size}>
      <path d="M4 7h10M4 12h16M4 17h7" />
    </Icon>
  );
}

export function IconCart({ size = 18 }: SizedProps) {
  return (
    <Icon size={size}>
      <path d="M4 5h2l2 11h10l2-8H7" />
      <circle cx="9" cy="20" r="1.2" />
      <circle cx="17" cy="20" r="1.2" />
    </Icon>
  );
}
