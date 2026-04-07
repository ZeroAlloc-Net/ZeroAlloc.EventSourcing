module.exports = {
  docsSidebar: [
    {
      type: 'category',
      label: 'Getting Started',
      items: [
        'getting-started/installation',
        'getting-started/first-aggregate',
        'getting-started/quick-start',
      ],
    },
    {
      type: 'category',
      label: 'Core Concepts',
      items: [
        'core-concepts/fundamentals',
        'core-concepts/events',
        'core-concepts/aggregates',
        'core-concepts/event-store',
        'core-concepts/snapshots',
        'core-concepts/projections',
        'core-concepts/consumers',
        'core-concepts/kafka-consumer',
        'core-concepts/architecture',
      ],
    },
    {
      type: 'category',
      label: 'Usage Guides',
      items: [
        'usage-guides/domain-modeling',
        'usage-guides/building-aggregates',
        'usage-guides/replay-rebuilding',
        'usage-guides/snapshots-usage',
        'usage-guides/projections-usage',
        'usage-guides/sql-adapters',
      ],
    },
    {
      type: 'category',
      label: 'Testing',
      items: [
        'testing/testing-aggregates',
        'testing/testing-events',
        'testing/testing-projections',
        'testing/integration-testing',
      ],
    },
    {
      type: 'category',
      label: 'Performance & Benchmarks',
      items: [
        'performance/characteristics',
        'performance/zero-allocation',
        'performance/optimization',
        'performance/benchmark-results',
      ],
    },
    'api-reference',
    {
      type: 'category',
      label: 'Advanced',
      items: [
        'advanced/custom-event-store',
        'advanced/custom-snapshots',
        'advanced/custom-projections',
        'advanced/plugin-architecture',
        'advanced/contributing',
      ],
    },
  ],
};
