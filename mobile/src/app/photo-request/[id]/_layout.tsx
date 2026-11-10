import { Redirect, Stack } from 'expo-router';

import { useRequestId } from '@/components/photoRequest/hooks';

/**
 * A coach's photo request: the intro, the closed notice and the screens where photos are added.
 * Route: /photo-request/[id]; an id that is not well formed (a broken link) goes back to Today.
 */
export default function PhotoRequestLayout() {
  const id = useRequestId();
  if (id === '') return <Redirect href="/" />;
  return <Stack screenOptions={{ headerShown: false }} />;
}
