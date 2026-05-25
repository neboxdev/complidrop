// CompliDrop logo — React component
// Drop into your codebase as components/Logo.jsx (or .tsx)
//
// Usage:
//   <Logo />                           // primary horizontal, navy wordmark
//   <Logo variant="twotone" />         // "Compli" navy + "Drop" sky (marketing hero)
//   <Logo variant="reverse" />         // white wordmark for dark backgrounds
//   <Logo variant="mark" size={32} />  // icon only
//   <Logo height={28} />               // pin height; width auto-scales
//
// Requires Plus Jakarta Sans loaded on the page (already is on complidrop.com).

import React from 'react';

const COLORS = {
  sky: '#0EA5E9',
  navy: '#0C4A6E',
  white: '#FFFFFF',
};

const DROPLET_PATH =
  'M50 4 C 50 4, 14 38, 14 62 C 14 82, 30 96, 50 96 C 70 96, 86 82, 86 62 C 86 38, 50 4, 50 4 Z';
const CHECK_PATH = 'M30 60 L 46 74 L 72 44';

function Mark({ size = 40, dropFill = COLORS.sky, checkStroke = COLORS.white }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 100 100"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
      focusable="false"
    >
      <path d={DROPLET_PATH} fill={dropFill} />
      <path
        d={CHECK_PATH}
        fill="none"
        stroke={checkStroke}
        strokeWidth="9"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/**
 * CompliDrop logo
 * @param {Object} props
 * @param {'primary'|'twotone'|'reverse'|'mark'} [props.variant='primary']
 * @param {number} [props.height=40]   Lockup height in px (icon size = height)
 * @param {number} [props.size]        Alias for height when variant='mark'
 * @param {string} [props.className]
 * @param {string} [props.title='CompliDrop']
 */
export default function Logo({
  variant = 'primary',
  height = 40,
  size,
  className,
  title = 'CompliDrop',
}) {
  if (variant === 'mark') {
    return (
      <span className={className} role="img" aria-label={title}>
        <Mark size={size ?? height} />
      </span>
    );
  }

  const iconSize = height;
  const fontSize = Math.round(height * 1.3); // wordmark visually balances at ~1.3x icon
  const gap = Math.round(height * 0.35);

  const wordmark = (() => {
    const baseStyle = {
      fontFamily: "'Plus Jakarta Sans', system-ui, sans-serif",
      fontWeight: 700,
      fontSize: `${fontSize}px`,
      letterSpacing: '-0.02em',
      lineHeight: 1,
      whiteSpace: 'nowrap',
    };
    if (variant === 'twotone') {
      return (
        <span style={{ ...baseStyle, color: COLORS.navy }}>
          Compli<span style={{ color: COLORS.sky }}>Drop</span>
        </span>
      );
    }
    if (variant === 'reverse') {
      return <span style={{ ...baseStyle, color: COLORS.white }}>CompliDrop</span>;
    }
    return <span style={{ ...baseStyle, color: COLORS.navy }}>CompliDrop</span>;
  })();

  return (
    <span
      className={className}
      role="img"
      aria-label={title}
      style={{ display: 'inline-flex', alignItems: 'center', gap: `${gap}px` }}
    >
      <Mark size={iconSize} />
      {wordmark}
    </span>
  );
}
