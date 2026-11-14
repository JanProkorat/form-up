import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { getTrainerProfile, profileKeys } from '@/api/profile';
import {
  archiveCoachPackage,
  coachPackagesKey,
  createCoachPackage,
  getCoachPackages,
  updateCoachPackage,
  type CreateCoachPackageRequest,
  type UpdateCoachPackageRequest,
} from '@/api/coach-packages';
import {
  archiveCoachDiscount,
  coachDiscountsKey,
  createCoachDiscount,
  getCoachDiscounts,
  updateCoachDiscount,
  type CreateCoachDiscountRequest,
  type UpdateCoachDiscountRequest,
} from '@/api/coach-discounts';

/** The coach's packages, archived ones included. Errors are handled by the page (paywall, retry). */
export function useCoachPackages(enabled = true) {
  return useQuery({ queryKey: coachPackagesKey, queryFn: getCoachPackages, retry: false, enabled });
}

/** The coach's discounts, archived ones included. */
export function useCoachDiscounts(enabled = true) {
  return useQuery({ queryKey: coachDiscountsKey, queryFn: getCoachDiscounts, retry: false, enabled });
}

/** The public profile's "from X" price with its billing unit, derived by the server in the coach's profile currency. */
export function useProfileFromPrice() {
  return useQuery({
    queryKey: profileKeys.trainer,
    queryFn: getTrainerProfile,
    retry: false,
    select: (profile) => ({
      price: profile.fromMonthlyPrice,
      currency: profile.fromMonthlyPriceCurrency,
      billing: profile.fromPriceBilling ?? undefined,
      intervalWeeks: profile.fromPriceIntervalWeeks ?? undefined,
    }),
  });
}

/**
 * A package change can alter which discounts apply and the public "from" price,
 * so both lists and the trainer profile refetch after every settled write.
 */
function useRefetchCatalog() {
  const queryClient = useQueryClient();
  return () =>
    Promise.all([
      queryClient.invalidateQueries({ queryKey: coachPackagesKey }),
      queryClient.invalidateQueries({ queryKey: coachDiscountsKey }),
      queryClient.invalidateQueries({ queryKey: profileKeys.trainer }),
    ]);
}

export function useCreatePackage() {
  const refetch = useRefetchCatalog();
  return useMutation({
    mutationFn: (request: CreateCoachPackageRequest) => createCoachPackage(request),
    onSettled: () => refetch(),
  });
}

export function useUpdatePackage() {
  const refetch = useRefetchCatalog();
  return useMutation({
    mutationFn: (variables: { packageId: string; request: UpdateCoachPackageRequest }) =>
      updateCoachPackage(variables.packageId, variables.request),
    onSettled: () => refetch(),
  });
}

export function useArchivePackage() {
  const refetch = useRefetchCatalog();
  return useMutation({
    mutationFn: (packageId: string) => archiveCoachPackage(packageId),
    onSettled: () => refetch(),
  });
}

export function useCreateDiscount() {
  const refetch = useRefetchCatalog();
  return useMutation({
    mutationFn: (request: CreateCoachDiscountRequest) => createCoachDiscount(request),
    onSettled: () => refetch(),
  });
}

export function useUpdateDiscount() {
  const refetch = useRefetchCatalog();
  return useMutation({
    mutationFn: (variables: { discountId: string; request: UpdateCoachDiscountRequest }) =>
      updateCoachDiscount(variables.discountId, variables.request),
    onSettled: () => refetch(),
  });
}

export function useArchiveDiscount() {
  const refetch = useRefetchCatalog();
  return useMutation({
    mutationFn: (discountId: string) => archiveCoachDiscount(discountId),
    onSettled: () => refetch(),
  });
}
