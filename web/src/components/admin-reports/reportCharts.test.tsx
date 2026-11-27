import { screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { AdminReportPeriod, type AdminReportMrrPointDto } from '@/api/generated';
import MrrChart from '@/components/admin-reports/MrrChart';
import PlansPanel from '@/components/admin-reports/PlansPanel';
import { renderWithProviders } from '@/test/render';

const point = (day: string, full: number, after: number): AdminReportMrrPointDto => ({
  day,
  full: [{ currency: 'CZK', amountMinorUnits: full }],
  afterPromotions: [{ currency: 'CZK', amountMinorUnits: after }],
});

function renderChart(series: AdminReportMrrPointDto[]) {
  return renderWithProviders([
    { path: '/', element: <MrrChart series={series} period={AdminReportPeriod.Days30} /> },
  ]);
}

describe('MrrChart x-axis labels', () => {
  it('anchors the last label to its end so a single point is not clipped at the right edge', async () => {
    renderChart([point('2026-11-20', 100000, 90000)]);
    const svg = await screen.findByRole('img');
    const labels = Array.from(svg.querySelectorAll('text')).filter((node) => node.getAttribute('y') === '212');
    expect(labels).toHaveLength(1);
    expect(labels[0].getAttribute('text-anchor')).toBe('end');
  });

  it('end-anchors only the last label and centres the others, for several points', async () => {
    renderChart([
      point('2026-11-01', 100000, 90000),
      point('2026-11-10', 110000, 95000),
      point('2026-11-20', 120000, 100000),
    ]);
    const svg = await screen.findByRole('img');
    const anchors = Array.from(svg.querySelectorAll('text'))
      .filter((node) => node.getAttribute('y') === '212')
      .map((node) => node.getAttribute('text-anchor'));
    expect(anchors).toEqual(['middle', 'middle', 'end']);
  });

  it('renders at a real pixel size, not a scaled viewBox, so text keeps its size on narrow screens', async () => {
    renderChart([point('2026-11-20', 100000, 90000)]);
    const svg = await screen.findByRole('img');
    expect(svg.hasAttribute('viewBox')).toBe(false);
    expect(svg.getAttribute('height')).toBe('220');
  });
});

describe('PlansPanel row names', () => {
  it('lets the add-on name wrap instead of truncating it', () => {
    renderWithProviders([{ path: '/', element: <PlansPanel plans={[]} addOns={[{ currency: 'CZK', count: 9, revenueMinorUnits: 269700 }]} /> }]);
    const name = screen.getByText('Second-role add-on');
    expect(name.className).not.toContain('truncate');
    expect(name.className).toContain('break-words');
  });
});
