import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useForm } from 'react-hook-form';
import type { FieldError } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Info, Activity, Loader2, Tag, Trash2, ChevronDown } from 'lucide-react';
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetFooter,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { getErrorCode } from '@/lib/api-errors';
import {
  FoodCategory,
  Allergen,
  DietaryPreference,
  FoodVisibility,
  type FoodSummary,
  type FoodTagDto,
  type CreateFoodRequest,
  type UpdateFoodRequest,
} from '@/api/food-types';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import {
  useCreateFood,
  useFood,
  useFoodStagedUploads,
  useReplaceFoodTagAssignments,
  useUpdateFood,
} from '@/hooks/useIngredientsQueries';
import MultiSelectPopover from '@/components/library/MultiSelectPopover';
import DeleteIngredientDialog from '@/components/ingredients/DeleteIngredientDialog';
import LibraryTagPickerPopover from '@/components/library/LibraryTagPickerPopover';
import VisibilityToggle from '@/components/library/VisibilityToggle';
import IngredientPicturesTab from '@/components/ingredients/IngredientPicturesTab';
import StagedPicturesField from '@/components/library/StagedPicturesField';
import { showPartialUploadError, uploadStagedPictures, useStagedPictures } from '@/components/library/pictureUpload';
import TagPill from '@/components/tags/TagPill';

const SELECT_CLASS =
  'flex h-10 w-full min-w-0 appearance-none rounded-field border border-input bg-background py-1 pr-9 pl-3 text-copy text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive';

/** Fixed unit keys for the drawer's Unit select (design-review MINOR finding #8,
 * `docs/design/ingredients/inventory.md`). The label is stored verbatim as
 * `CommonServings[0].Label`; display translates the key and falls back to the
 * raw string for legacy seed data whose label doesn't match any key.
 * Must mirror ServingUnits.cs (backend/FormUp.Application/Domain/Constants) key-for-key. */
export const UNIT_KEYS = [
  'portion',
  'piece',
  'slice',
  'cup',
  'tbsp',
  'tsp',
  'handful',
  'glass',
  'pack',
  'can',
  'tub',
  'head',
  'clove',
  'bunch',
  'scoop',
] as const;
export type UnitKey = (typeof UNIT_KEYS)[number];

type TabValue = 'details' | 'pictures';

const CATEGORY_VALUES = Object.values(FoodCategory);
const ALLERGEN_VALUES = Object.values(Allergen);
const DIETARY_PREFERENCE_VALUES = Object.values(DietaryPreference);

// Numeric fields use `z.number()` (not `z.coerce.number()`) paired with
// `register(name, { valueAsNumber: true })` below — RHF converts the input
// string to a number before validation runs, so the schema's input and
// output types stay identical. `z.coerce.number()` would make the schema's
// *input* type `unknown` while its *output* type is `number`, which
// `@hookform/resolvers`'s `Resolver<FormValues>` cannot reconcile with a
// `useForm<FormValues>()` typed to the output shape (TS2322/TS2345 at build).
//
// `allowedLegacyUnit` — an owned food's CommonServings[0].Label may predate
// the fixed UNIT_KEYS list (e.g. imported/seeded data). The Unit select
// renders that raw label as an extra option (see `unit` state below) so it's
// visibly selected, but without this the schema itself would still reject
// saving the food unchanged, since the raw label isn't a UNIT_KEYS member.
// Passed only in edit mode (see `IngredientDrawer`'s `legacyUnit` local) —
// on create there's no existing food to inherit a legacy label from.
function buildFormSchema(allowedLegacyUnit: string | null) {
  return z.object({
    name: z.string().trim().min(1),
    category: z.string().refine((value) => CATEGORY_VALUES.includes(value as FoodCategory), { message: 'required' }),
    kcal: z.number({ message: 'required' }).min(0),
    protein: z.number({ message: 'required' }).min(0),
    carbs: z.number({ message: 'required' }).min(0),
    fat: z.number({ message: 'required' }).min(0),
    fiber: z.number().min(0).optional(),
    unit: z
      .string()
      .refine(
        (value) => (UNIT_KEYS as readonly string[]).includes(value) || (allowedLegacyUnit !== null && value === allowedLegacyUnit),
        { message: 'required' },
      ),
    servingSize: z.number({ message: 'required' }).positive(),
    visibility: z.enum(['Private', 'Public']),
    dietaryPreferences: z.array(z.string()),
    allergens: z.array(z.string()),
  });
}

type FormValues = z.infer<ReturnType<typeof buildFormSchema>>;

// Unit defaults to "Portion" on create (docs/design/ingredients/inventory.md
// point 10, "select, value 'Portion'") — every other field starts empty.
const EMPTY_VALUES: FormValues = {
  name: '',
  category: '',
  kcal: undefined as unknown as number,
  protein: undefined as unknown as number,
  carbs: undefined as unknown as number,
  fat: undefined as unknown as number,
  fiber: undefined,
  unit: 'portion',
  servingSize: undefined as unknown as number,
  visibility: 'Private',
  dietaryPreferences: [],
  allergens: [],
};

function valuesFromFood(food: FoodSummary): FormValues {
  const defaultServing = food.commonServings?.[0];
  return {
    // `food.name` is resolved for the request's Accept-Language (FoodSummary.cs);
    // `rawName` is the canonical, language-independent name actually stored on
    // the document. Loading `name` here would round-trip the translated
    // string back through onSubmit's UpdateFoodRequest.name on an untouched
    // save, silently overwriting the base name with a translation.
    name: food.rawName ?? food.name ?? '',
    category: food.category ?? '',
    kcal: food.nutrientValue?.kcal ?? (undefined as unknown as number),
    protein: food.nutrientValue?.protein ?? (undefined as unknown as number),
    carbs: food.nutrientValue?.carbs ?? (undefined as unknown as number),
    fat: food.nutrientValue?.fat ?? (undefined as unknown as number),
    fiber: food.nutrientValue?.fiber,
    unit: defaultServing?.label ?? '',
    servingSize: defaultServing?.weightGrams ?? (undefined as unknown as number),
    visibility: food.visibility === FoodVisibility.Public ? 'Public' : 'Private',
    dietaryPreferences: (food.dietaryPreferences ?? []) as string[],
    allergens: (food.allergens ?? []) as string[],
  };
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The food to edit or view. `null` opens the drawer in create mode. */
  food: FoodSummary | null;
  /** True for a system/shared food, or for a trainer-only coach viewing any row. */
  readOnly: boolean;
  /** Gates the coach-private tag section (#1120) — kept separate from
   * `readOnly` since a nutritionist can tag any visible food, even a read-only one. */
  isNutritionist: boolean;
  /** System foods are always public, so an admin-only account gets no visibility control. */
  hideVisibility?: boolean;
}

/**
 * Create / edit / read-only drawer for an ingredient. Mirrors `AddClientDrawer`
 * (`@/components/clients/AddClientDrawer.tsx`) — Sheet + React Hook Form + Zod.
 * On error, `onSubmit`'s per-call `onError` sets an additive inline error for
 * the one code that has a natural field to attach to (KCAL_INCONSISTENT →
 * the Calories field); the mutation hooks themselves (`useCreateFood`/
 * `useUpdateFood`) toast every *other* error code, skipping KCAL_INCONSISTENT
 * specifically so it isn't shown twice.
 *
 * Editing is a full-state PUT (`UpdateFoodEndpoint.cs:61-90`) — `onSubmit`
 * round-trips `nameEn`/`nameCs`/`nameDe`, `note`,
 * `nutrientValue`'s `sugar`/`saturatedFat`/`salt`, and any `commonServings`
 * beyond the default (index 0) straight from the loaded `food`, since the
 * drawer's own fields never touch them.
 */
export default function IngredientDrawer({
  open,
  onOpenChange,
  food,
  readOnly,
  isNutritionist,
  hideVisibility = false,
}: Props) {
  const { t } = useTranslation();
  const mode = food === null ? 'create' : readOnly ? 'view' : 'edit';
  const [deleteOpen, setDeleteOpen] = useState(false);
  // Local copy of the food's tag chips (#1120), so assigning re-renders
  // immediately instead of waiting on the parent to refetch `selectedFood`.
  const [assignedTags, setAssignedTags] = useState<FoodTagDto[]>([]);

  const [tab, setTab] = useState<TabValue>('details');
  const staged = useStagedPictures();
  const [isUploadingPictures, setIsUploadingPictures] = useState(false);

  const createMutation = useCreateFood();
  const updateMutation = useUpdateFood();
  const replaceTagsMutation = useReplaceFoodTagAssignments();
  const uploads = useFoodStagedUploads();
  const isPending = createMutation.isPending || updateMutation.isPending || isUploadingPictures;

  // The Pictures tab reads the loaded food so a refetch after each picture
  // action keeps it current; the form itself keeps seeding from the list snapshot.
  const foodQuery = useFood(open ? food?.foodId : undefined, food ?? undefined);
  const picturesFood = foodQuery.data ?? food;

  // Closing — by any route — drops staged previews; nothing closes mid-upload.
  function handleOpenChange(next: boolean) {
    if (!next) {
      if (isUploadingPictures) {
        return;
      }
      staged.clear();
    }
    onOpenChange(next);
  }

  function onInvalid() {
    setTab('details');
  }

  // Only in edit mode: lets the schema accept the food's own current Unit
  // label unchanged even when it predates UNIT_KEYS — see buildFormSchema's
  // doc comment.
  const legacyUnit = mode === 'edit' ? (food?.commonServings?.[0]?.label ?? null) : null;
  const formSchema = useMemo(() => buildFormSchema(legacyUnit), [legacyUnit]);

  // True once the user types Calories; stops create-mode auto-fill until the next open.
  const kcalManualRef = useRef(false);

  const {
    register,
    handleSubmit,
    reset,
    setError,
    watch,
    setValue,
    getValues,
    clearErrors,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(formSchema),
    mode: 'onTouched',
    defaultValues: EMPTY_VALUES,
  });

  // Fresh form state per open, sourced from the food currently selected —
  // same "adjust on open" pattern as AddClientDrawer, not a useMemo synced
  // to `food` alone, since re-opening the SAME food (e.g. after a failed
  // save) should also re-sync from the latest query data.
  useEffect(() => {
    if (open) {
      reset(food ? valuesFromFood(food) : EMPTY_VALUES);
      setAssignedTags(food?.tags ?? []);
      kcalManualRef.current = false;
    }
  }, [open, food, reset]);

  // A fresh open always starts on Details.
  useEffect(() => {
    if (open) {
      setTab('details');
    }
  }, [open]);

  const dietaryPreferences = watch('dietaryPreferences');
  const allergens = watch('allergens');
  const unit = watch('unit');
  const visibility = watch('visibility');

  // Picks the right translated message for one of the four macro fields'
  // error, by RHF's error `type` — never the raw zod message:
  //   'server'    — KCAL_INCONSISTENT, set via setError() below; message is
  //                 already the translated apiErrors.KCAL_INCONSISTENT text.
  //   'too_small' — zod's `.min(0)` failed on a present-but-negative value;
  //                 a different message than "required" (the value IS there).
  //   anything else (zod's invalid_type, e.g. empty/NaN) — the field is
  //                 missing, so the translated "<field> is required." text.
  function macroFieldError(error: FieldError | undefined, requiredKey: string): string | null {
    if (!error) {
      return null;
    }
    if (error.type === 'server') {
      return error.message ?? null;
    }
    if (error.type === 'too_small') {
      return t('ingredients.drawer.macroMinRequired');
    }
    return t(requiredKey);
  }

  // Fiber is optional, so its only possible error is the negative-value
  // check — there is no "required" case to translate.
  function fiberFieldError(error: FieldError | undefined): string | null {
    if (!error || error.type !== 'too_small') {
      return null;
    }
    return t('ingredients.drawer.macroMinRequired');
  }

  function numericOrZero(value: number | undefined): number {
    return value === undefined || Number.isNaN(value) ? 0 : value;
  }

  function handleKcalChange() {
    kcalManualRef.current = true;
  }

  // Registered as each macro field's register(..., { onChange }) — not
  // watch()+useEffect, since StrictMode's double-invoked effects and this
  // drawer's own reset() on open would both re-trigger a watch-based
  // recompute. reset()/setValue() never fire onChange, so opening a food
  // (or re-opening after a failed save) can't reach this handler.
  function handleMacroChange() {
    if (mode !== 'create' || kcalManualRef.current) {
      return;
    }
    const { protein, carbs, fat, fiber } = getValues();
    const allEmpty = [protein, carbs, fat, fiber].every((value) => value === undefined || Number.isNaN(value));
    if (allEmpty) {
      return;
    }
    const computedKcal = Math.round(
      numericOrZero(protein) * 4 + numericOrZero(carbs) * 4 + numericOrZero(fat) * 9 + numericOrZero(fiber) * 2,
    );
    setValue('kcal', computedKcal, { shouldValidate: true });
    clearErrors('kcal');
  }

  // The food is saved first; its staged pictures upload afterwards. A failed
  // picture never undoes the save — the drawer still closes with one toast.
  async function createWithPictures(request: CreateFoodRequest) {
    try {
      const created = await createMutation.mutateAsync(request);
      if (created.foodId && (staged.main || staged.extras.length > 0)) {
        const foodId = created.foodId;
        setIsUploadingPictures(true);
        let failed: number;
        try {
          failed = await uploadStagedPictures(
            staged,
            (file) => uploads.uploadMain(foodId, file),
            (file) => uploads.uploadGallery(foodId, file),
          );
        } finally {
          setIsUploadingPictures(false);
        }
        if (failed > 0) {
          showPartialUploadError(failed);
        }
      }
      staged.clear();
      onOpenChange(false);
    } catch (error) {
      if (getErrorCode(error) === 'KCAL_INCONSISTENT') {
        setError('kcal', { type: 'server', message: t('apiErrors.KCAL_INCONSISTENT') });
        setTab('details');
      }
    }
  }

  function onSubmit(values: FormValues) {
    const commonServings = [
      { label: values.unit, weightGrams: values.servingSize },
      ...(food?.commonServings ?? []).slice(1),
    ];

    if (mode === 'create') {
      const request: CreateFoodRequest = {
        name: values.name,
        nutrientValue: {
          kcal: values.kcal,
          protein: values.protein,
          carbs: values.carbs,
          fat: values.fat,
          fiber: values.fiber,
        },
        category: values.category as FoodCategory,
        visibility: values.visibility as FoodVisibility,
        allergens: values.allergens as Allergen[],
        dietaryPreferences: values.dietaryPreferences as DietaryPreference[],
        commonServings,
      };
      void createWithPictures(request);
      return;
    }

    if (!food?.foodId) {
      return;
    }

    const request: UpdateFoodRequest = {
      name: values.name,
      nameEn: food.nameEn,
      nameCs: food.nameCs,
      nameDe: food.nameDe,
      nutrientValue: {
        kcal: values.kcal,
        protein: values.protein,
        carbs: values.carbs,
        fat: values.fat,
        fiber: values.fiber,
        sugar: food.nutrientValue?.sugar,
        saturatedFat: food.nutrientValue?.saturatedFat,
        salt: food.nutrientValue?.salt,
      },
      category: values.category as FoodCategory,
      note: food.note,
      visibility: values.visibility as FoodVisibility,
      allergens: values.allergens as Allergen[],
      dietaryPreferences: values.dietaryPreferences as DietaryPreference[],
      commonServings,
    };
    updateMutation.mutate(
      { foodId: food.foodId, request },
      {
        onSuccess: () => onOpenChange(false),
        onError: (error) => {
          if (getErrorCode(error) === 'KCAL_INCONSISTENT') {
            setError('kcal', { type: 'server', message: t('apiErrors.KCAL_INCONSISTENT') });
          }
        },
      },
    );
  }

  return (
    <>
      <Sheet open={open} onOpenChange={handleOpenChange}>
        <SheetContent className="sm:w-drawer sm:max-w-none">
          <SheetHeader className="gap-1 border-b border-border p-6">
            <SheetTitle className="text-auth-title font-semibold">
              {t(
                mode === 'create'
                  ? 'ingredients.drawer.createTitle'
                  : mode === 'view'
                    ? 'ingredients.drawer.viewTitle'
                    : 'ingredients.drawer.editTitle',
              )}
            </SheetTitle>
            <SheetDescription className="text-body">
              {t(
                mode === 'create'
                  ? 'ingredients.drawer.subtitleCreate'
                  : mode === 'view'
                    ? 'ingredients.drawer.subtitleView'
                    : 'ingredients.drawer.subtitleEdit',
              )}
            </SheetDescription>
          </SheetHeader>

          <form onSubmit={handleSubmit(onSubmit, onInvalid)} className="flex min-h-0 flex-1 flex-col px-6">
            <Tabs value={tab} onValueChange={(value) => setTab(value as TabValue)} className="min-h-0 flex-1 gap-4">
              <TabsList variant="underline" aria-label={t('ingredients.drawer.tabsLabel')}>
                <TabsTrigger variant="underline" value="details">
                  {t('ingredients.drawer.tabDetails')}
                </TabsTrigger>
                <TabsTrigger variant="underline" value="pictures">
                  {t('ingredients.drawer.tabPictures')}
                </TabsTrigger>
              </TabsList>

              <TabsContent value="details" className="flex flex-col gap-6 overflow-y-auto pb-4">
            <fieldset disabled={readOnly} className="flex flex-col gap-6">
              <div className="flex flex-col gap-3">
                <h3 className="flex items-center gap-2 text-copy font-semibold text-foreground">
                  <Info className="size-4.25 text-nutrition" aria-hidden="true" />
                  {t('ingredients.drawer.basicInfo')}
                </h3>
                <div className="grid grid-cols-2 gap-4">
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-name">
                      {t('ingredients.drawer.name')} <span className="text-destructive">*</span>
                    </Label>
                    <Input
                      id="ingredient-name"
                      {...register('name')}
                      aria-invalid={!!errors.name}
                      placeholder={t('ingredients.drawer.namePlaceholder')}
                      className="h-10 text-copy"
                    />
                    {errors.name && <p className="text-meta text-destructive">{t('ingredients.drawer.nameRequired')}</p>}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-category">
                      {t('ingredients.drawer.category')} <span className="text-destructive">*</span>
                    </Label>
                    <div className="relative">
                      <select
                        id="ingredient-category"
                        {...register('category')}
                        aria-invalid={!!errors.category}
                        className={SELECT_CLASS}
                      >
                        <option value="">{t('ingredients.drawer.categoryPlaceholder')}</option>
                        {CATEGORY_VALUES.map((category) => (
                          <option key={category} value={category}>
                            {t(`ingredients.category.${category}`)}
                          </option>
                        ))}
                      </select>
                      <ChevronDown
                        className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground"
                        aria-hidden="true"
                      />
                    </div>
                    {errors.category && (
                      <p className="text-meta text-destructive">{t('ingredients.drawer.categoryRequired')}</p>
                    )}
                  </div>
                </div>
              </div>

              <div className="flex flex-col gap-3">
                <h3 className="flex items-center gap-2 text-copy font-semibold text-foreground">
                  <Activity className="size-4.25 text-nutrition" aria-hidden="true" />
                  {t('ingredients.drawer.nutritionalInfo')}
                </h3>
                <div className="grid grid-cols-2 gap-3">
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-protein">
                      {t('ingredients.drawer.protein')} <span className="text-destructive">*</span>
                    </Label>
                    <Input
                      id="ingredient-protein"
                      type="number"
                      step="any"
                      {...register('protein', { valueAsNumber: true, onChange: handleMacroChange })}
                      aria-invalid={!!errors.protein}
                      placeholder={t('ingredients.drawer.zeroPlaceholder')}
                      className="h-10 text-copy"
                    />
                    {macroFieldError(errors.protein, 'ingredients.drawer.proteinRequired') && (
                      <p className="text-meta text-destructive">
                        {macroFieldError(errors.protein, 'ingredients.drawer.proteinRequired')}
                      </p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-carbs">
                      {t('ingredients.drawer.carbs')} <span className="text-destructive">*</span>
                    </Label>
                    <Input
                      id="ingredient-carbs"
                      type="number"
                      step="any"
                      {...register('carbs', { valueAsNumber: true, onChange: handleMacroChange })}
                      aria-invalid={!!errors.carbs}
                      placeholder={t('ingredients.drawer.zeroPlaceholder')}
                      className="h-10 text-copy"
                    />
                    {macroFieldError(errors.carbs, 'ingredients.drawer.carbsRequired') && (
                      <p className="text-meta text-destructive">
                        {macroFieldError(errors.carbs, 'ingredients.drawer.carbsRequired')}
                      </p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-fat">
                      {t('ingredients.drawer.fat')} <span className="text-destructive">*</span>
                    </Label>
                    <Input
                      id="ingredient-fat"
                      type="number"
                      step="any"
                      {...register('fat', { valueAsNumber: true, onChange: handleMacroChange })}
                      aria-invalid={!!errors.fat}
                      placeholder={t('ingredients.drawer.zeroPlaceholder')}
                      className="h-10 text-copy"
                    />
                    {macroFieldError(errors.fat, 'ingredients.drawer.fatRequired') && (
                      <p className="text-meta text-destructive">
                        {macroFieldError(errors.fat, 'ingredients.drawer.fatRequired')}
                      </p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-fiber">{t('ingredients.drawer.fiber')}</Label>
                    <Input
                      id="ingredient-fiber"
                      type="number"
                      step="any"
                      {...register('fiber', {
                        setValueAs: (value: string) => (value === '' ? undefined : Number(value)),
                        onChange: handleMacroChange,
                      })}
                      aria-invalid={!!errors.fiber}
                      placeholder={t('ingredients.drawer.zeroPlaceholder')}
                      className="h-10 text-copy"
                    />
                    {fiberFieldError(errors.fiber) && (
                      <p className="text-meta text-destructive">{fiberFieldError(errors.fiber)}</p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-kcal">
                      {t('ingredients.drawer.calories')} <span className="text-destructive">*</span>
                    </Label>
                    <Input
                      id="ingredient-kcal"
                      type="number"
                      step="any"
                      {...register('kcal', { valueAsNumber: true, onChange: handleKcalChange })}
                      aria-invalid={!!errors.kcal}
                      placeholder={t('ingredients.drawer.zeroPlaceholder')}
                      className="h-10 text-copy"
                    />
                    {macroFieldError(errors.kcal, 'ingredients.drawer.caloriesRequired') && (
                      <p className="text-meta text-destructive">
                        {macroFieldError(errors.kcal, 'ingredients.drawer.caloriesRequired')}
                      </p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-unit">
                      {t('ingredients.drawer.unit')} <span className="text-destructive">*</span>
                    </Label>
                    <div className="relative">
                      <select
                        id="ingredient-unit"
                        {...register('unit')}
                        aria-invalid={!!errors.unit}
                        className={SELECT_CLASS}
                      >
                        <option value="">{t('ingredients.drawer.unitPlaceholder')}</option>
                        {UNIT_KEYS.map((key) => (
                          <option key={key} value={key}>
                            {t(`ingredients.unit.${key}`)}
                          </option>
                        ))}
                        {unit && !(UNIT_KEYS as readonly string[]).includes(unit) && (
                          <option value={unit}>{unit}</option>
                        )}
                      </select>
                      <ChevronDown
                        className="pointer-events-none absolute top-1/2 right-3 size-4 -translate-y-1/2 text-muted-foreground"
                        aria-hidden="true"
                      />
                    </div>
                    {errors.unit && <p className="text-meta text-destructive">{t('ingredients.drawer.unitRequired')}</p>}
                  </div>
                </div>
                <div className="grid grid-cols-2 gap-4">
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-serving-size">
                      {t('ingredients.drawer.servingSize')} <span className="text-destructive">*</span>
                    </Label>
                    <Input
                      id="ingredient-serving-size"
                      type="number"
                      step="any"
                      {...register('servingSize', { valueAsNumber: true })}
                      aria-invalid={!!errors.servingSize}
                      placeholder={t('ingredients.drawer.zeroPlaceholder')}
                      className="h-10 text-copy"
                    />
                    {errors.servingSize && (
                      <p className="text-meta text-destructive">{t('ingredients.drawer.servingSizeRequired')}</p>
                    )}
                  </div>
                </div>
              </div>

              {!readOnly && !hideVisibility && (
                <VisibilityToggle
                  id="ingredient-visibility"
                  entity="ingredient"
                  value={visibility}
                  onChange={(next) => setValue('visibility', next, { shouldDirty: true })}
                />
              )}

              <div className="flex flex-col gap-3">
                <h3 className="flex items-center gap-2 text-copy font-semibold text-foreground">
                  <Tag className="size-4.25 text-nutrition" aria-hidden="true" />
                  {t('ingredients.drawer.tagsClassification')}
                </h3>
                <div className="grid grid-cols-2 gap-4">
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-dietary-preferences">{t('ingredients.drawer.dietaryPreferences')}</Label>
                    <MultiSelectPopover
                      id="ingredient-dietary-preferences"
                      placeholder={t('ingredients.drawer.dietaryPreferencesPlaceholder')}
                      options={DIETARY_PREFERENCE_VALUES.map((value) => ({
                        value,
                        label: t(`ingredients.dietaryPreference.${value}`),
                      }))}
                      selected={dietaryPreferences}
                      onChange={(values) => setValue('dietaryPreferences', values)}
                    />
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="ingredient-allergens">{t('ingredients.drawer.allergens')}</Label>
                    <MultiSelectPopover
                      id="ingredient-allergens"
                      tone="error"
                      placeholder={t('ingredients.drawer.allergensPlaceholder')}
                      options={ALLERGEN_VALUES.map((value) => ({ value, label: t(`ingredients.allergen.${value}`) }))}
                      selected={allergens}
                      onChange={(values) => setValue('allergens', values)}
                    />
                  </div>
                </div>
              </div>
            </fieldset>

            {/* Coach-private food tags (#1120) — deliberately OUTSIDE the
                fieldset above: a nutritionist can tag ANY visible food
                (system, shared, own), even one whose fields are read-only.
                Only rendered for a nutritionist — a trainer-only coach gets
                no tag UI at all, since only a nutritionist can own a food
                tag. Only shown once the food actually exists (not on
                create) — tag assignment needs a real FoodId. */}
            {isNutritionist && food?.foodId && (
              <div className="flex flex-col gap-3 border-t border-border pt-6">
                <h3 className="flex items-center gap-2 text-copy font-semibold text-foreground">
                  <Tag className="size-4.25 text-nutrition" aria-hidden="true" />
                  {t('ingredients.drawer.myTags')}
                </h3>
                <div className="flex flex-wrap items-center gap-2">
                  {assignedTags.map((tag) => (
                    <TagPill key={tag.tagId} name={tag.name ?? ''} colorHex={tag.colorHex} />
                  ))}
                  <LibraryTagPickerPopover
                    assignedTags={assignedTags}
                    isReplacing={replaceTagsMutation.isPending}
                    onReplace={(tagIds) =>
                      food.foodId &&
                      replaceTagsMutation.mutate(
                        { foodId: food.foodId, tagIds },
                        { onSuccess: (response) => setAssignedTags(response.tags ?? []) },
                      )
                    }
                  />
                </div>
              </div>
            )}
              </TabsContent>

              {/* Picture area — outside the Details fieldset, same reasoning as
                  the tags section: its own `readOnly` prop gates the controls,
                  independent of the form fieldset's disabled state. */}
              <TabsContent value="pictures" className="flex flex-col overflow-y-auto pb-4">
                {food?.foodId ? (
                  <IngredientPicturesTab
                    foodId={food.foodId}
                    imageUrl={picturesFood?.imageUrl}
                    galleryImageUrls={picturesFood?.galleryImageUrls ?? []}
                    readOnly={readOnly}
                  />
                ) : (
                  <StagedPicturesField
                    staged={staged}
                    disabled={isUploadingPictures}
                    alt={t('ingredients.picture.alt')}
                    testIdPrefix="ingredient-gallery"
                  />
                )}
              </TabsContent>
            </Tabs>
          </form>

          <SheetFooter className="flex-row justify-between border-t border-line p-6">
            {mode === 'edit' ? (
              <Button
                type="button"
                variant="destructive"
                size="lg"
                className="font-semibold"
                onClick={() => setDeleteOpen(true)}
              >
                <Trash2 aria-hidden="true" />
                {t('ingredients.drawer.delete')}
              </Button>
            ) : (
              <span />
            )}
            <div className="flex gap-2">
              <Button
                type="button"
                variant="outline"
                size="lg"
                className="font-semibold"
                disabled={isUploadingPictures}
                onClick={() => handleOpenChange(false)}
              >
                {mode === 'view' ? t('common.close') : t('common.cancel')}
              </Button>
              {mode !== 'view' && (
                <Button
                  type="button"
                  size="lg"
                  className="px-4 font-semibold"
                  disabled={isPending}
                  onClick={handleSubmit(onSubmit, onInvalid)}
                >
                  {isUploadingPictures && <Loader2 className="animate-spin" aria-hidden="true" />}
                  {t('ingredients.drawer.save')}
                </Button>
              )}
            </div>
          </SheetFooter>
        </SheetContent>
      </Sheet>
      {food?.foodId && (
        <DeleteIngredientDialog
          open={deleteOpen}
          onOpenChange={setDeleteOpen}
          foodId={food.foodId}
          foodName={food.name ?? ''}
          onDeleted={() => {
            setDeleteOpen(false);
            onOpenChange(false);
          }}
        />
      )}
    </>
  );
}
