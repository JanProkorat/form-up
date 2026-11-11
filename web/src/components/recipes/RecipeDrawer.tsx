import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useForm } from 'react-hook-form';
import { zodResolver } from '@hookform/resolvers/zod';
import { z } from 'zod';
import { Loader2, Tag } from 'lucide-react';
import { Sheet, SheetContent, SheetDescription, SheetFooter, SheetHeader, SheetTitle } from '@/components/ui/sheet';
import { Button } from '@/components/ui/button';
import { Badge } from '@/components/ui/badge';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Skeleton } from '@/components/ui/skeleton';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { Textarea } from '@/components/ui/textarea';
import {
  Allergen,
  DietaryPreference,
  RecipeDifficulty,
  RecipeMealType,
  RecipeVisibility,
  type CreateRecipeRequest,
  type GetRecipeResponse,
  type RecipeStepDto,
  type RecipeSummaryDto,
  type UpdateRecipeRequest,
} from '@/api/recipe-types';
import type { FoodTagDto } from '@/api/food-types';
import {
  useCreateRecipe,
  useRecipe,
  useRecipeStagedUploads,
  useReplaceRecipeTagAssignments,
  useUpdateRecipe,
} from '@/hooks/useRecipesQueries';
import MultiSelectPopover from '@/components/library/MultiSelectPopover';
import LibraryTagPickerPopover from '@/components/library/LibraryTagPickerPopover';
import VisibilityToggle from '@/components/library/VisibilityToggle';
import TagPill from '@/components/tags/TagPill';
import RecipeIngredientsTab from '@/components/recipes/RecipeIngredientsTab';
import RecipePreparationTab from '@/components/recipes/RecipePreparationTab';
import RecipePicturesTab from '@/components/recipes/RecipePicturesTab';
import StagedPicturesField from '@/components/library/StagedPicturesField';
import { showPartialUploadError, uploadStagedPictures, useStagedPictures } from '@/components/library/pictureUpload';
import DeleteRecipeDialog from '@/components/recipes/DeleteRecipeDialog';
import {
  isValidAmount,
  isValidMinutesText,
  isValidStepTitle,
  linesFromRecipe,
  parseAmount,
  parseMinutes,
  stepsFromRecipe,
  type IngredientLine,
  type StepItem,
} from '@/components/recipes/recipe-form-types';

const MEAL_TYPE_VALUES = Object.values(RecipeMealType);
const DIFFICULTY_VALUES = Object.values(RecipeDifficulty);
const DIETARY_PREFERENCE_VALUES = Object.values(DietaryPreference);
const ALLERGEN_ORDER = Object.values(Allergen);

const SELECT_CLASS =
  'flex h-10 w-full min-w-0 rounded-md border border-input bg-background px-3 py-1 text-body text-foreground outline-none focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 aria-invalid:border-destructive';

type TabValue = 'details' | 'ingredients' | 'preparation' | 'pictures';

// react-hook-form also runs setValueAs on the field's default value, which can be
// a number or the API's null — not only the input's string.
function optionalMinutes(value: unknown): number | undefined {
  if (typeof value === 'number') {
    return value;
  }
  if (typeof value !== 'string' || value.trim() === '') {
    return undefined;
  }
  return Number(value);
}

const formSchema = z.object({
  name: z.string().trim().min(1),
  servings: z.number({ message: 'required' }).int().min(1),
  description: z.string(),
  visibility: z.enum(['Private', 'Public']),
  mealTypes: z.array(z.string()).min(1),
  difficulty: z.string(),
  prepTimeMinutes: z.number().int().min(0).optional(),
  cookTimeMinutes: z.number().int().min(0).optional(),
  dietaryPreferences: z.array(z.string()),
});

type FormValues = z.infer<typeof formSchema>;

const EMPTY_VALUES: FormValues = {
  name: '',
  servings: 1,
  description: '',
  visibility: 'Private',
  mealTypes: [],
  difficulty: '',
  prepTimeMinutes: undefined,
  cookTimeMinutes: undefined,
  dietaryPreferences: [],
};

function valuesFromRecipe(recipe: GetRecipeResponse): FormValues {
  return {
    name: recipe.name ?? '',
    servings: recipe.servings ?? 1,
    description: recipe.description ?? '',
    visibility: recipe.visibility === RecipeVisibility.Public ? 'Public' : 'Private',
    mealTypes: (recipe.mealTypes ?? []) as string[],
    difficulty: recipe.difficulty ?? '',
    prepTimeMinutes: recipe.prepTimeMinutes ?? undefined,
    cookTimeMinutes: recipe.cookTimeMinutes ?? undefined,
    dietaryPreferences: (recipe.dietaryPreferences ?? []) as string[],
  };
}

/**
 * Allergens shown read-only on the Details tab. A line picked in this session
 * carries its food's own allergens; a line loaded from a saved recipe does
 * not (the API only returns the recipe-level union). While the ingredient set
 * is unchanged the server's union is shown as is; once it changed, the
 * picked lines' allergens are merged with the server's union — a superset on
 * purpose (never under-report an allergen) until the save returns the real
 * derived list.
 */
function deriveAllergens(lines: IngredientLine[], recipe: GetRecipeResponse | null): Allergen[] {
  const known = new Set<Allergen>();
  for (const line of lines) {
    for (const allergen of line.allergens ?? []) {
      known.add(allergen);
    }
  }
  if (recipe) {
    const savedIds = new Set((recipe.foods ?? []).map((food) => food.foodExternalId));
    const unchanged = lines.length === savedIds.size && lines.every((line) => savedIds.has(line.foodId));
    if (unchanged || lines.some((line) => line.allergens === null)) {
      for (const allergen of recipe.allergens ?? []) {
        known.add(allergen);
      }
    }
  }
  return ALLERGEN_ORDER.filter((allergen) => known.has(allergen));
}

interface FormProps {
  /** The loaded recipe, or `null` when creating. */
  recipe: GetRecipeResponse | null;
  mode: 'create' | 'edit' | 'view';
  onClose: () => void;
  onDeleteClick: () => void;
  /** Reports whether staged pictures are uploading, so the drawer can refuse to close meanwhile. */
  onBusyChange: (busy: boolean) => void;
}

/**
 * The drawer's form + footer. Mounted only once its data exists (see
 * `RecipeDrawer`), so every piece of state below is a plain initializer — no
 * reset effects, and a background refetch of the recipe never overwrites
 * what the coach is typing.
 */
function RecipeForm({ recipe, mode, onClose, onDeleteClick, onBusyChange }: FormProps) {
  const { t } = useTranslation();
  const readOnly = mode === 'view';
  const [tab, setTab] = useState<TabValue>('details');
  const [lines, setLines] = useState<IngredientLine[]>(() => (recipe ? linesFromRecipe(recipe) : []));
  const [steps, setSteps] = useState<StepItem[]>(() => (recipe ? stepsFromRecipe(recipe) : []));
  const [showIngredientErrors, setShowIngredientErrors] = useState(false);
  const [showStepErrors, setShowStepErrors] = useState(false);

  const createMutation = useCreateRecipe();
  const updateMutation = useUpdateRecipe();
  const uploads = useRecipeStagedUploads();
  const staged = useStagedPictures();
  const [isUploadingPictures, setIsUploadingPictures] = useState(false);
  const isPending = createMutation.isPending || updateMutation.isPending || isUploadingPictures;

  function setUploading(uploading: boolean) {
    setIsUploadingPictures(uploading);
    onBusyChange(uploading);
  }

  // Local copy of the recipe's tag chips, so assigning re-renders immediately
  // without refetching the detail (which would be a no-op for this snapshot form).
  const [assignedTags, setAssignedTags] = useState<FoodTagDto[]>(() => recipe?.tags ?? []);
  const replaceTagsMutation = useReplaceRecipeTagAssignments();

  function replaceTags(tagIds: string[]) {
    if (!recipe?.recipeId) {
      return;
    }
    replaceTagsMutation.mutate(
      { recipeId: recipe.recipeId, tagIds },
      { onSuccess: (response) => setAssignedTags(response.tags ?? []) },
    );
  }

  const {
    register,
    handleSubmit,
    watch,
    setValue,
    formState: { errors },
  } = useForm<FormValues>({
    resolver: zodResolver(formSchema),
    mode: 'onTouched',
    defaultValues: recipe ? valuesFromRecipe(recipe) : EMPTY_VALUES,
  });

  const visibility = watch('visibility');
  const mealTypes = watch('mealTypes');
  const dietaryPreferences = watch('dietaryPreferences');
  const servings = watch('servings');
  const prepTime = watch('prepTimeMinutes');
  const cookTime = watch('cookTimeMinutes');

  const allergens = deriveAllergens(lines, recipe);
  const timesSummary =
    prepTime || cookTime
      ? t('recipes.preparation.times', {
          prep: t('recipes.table.minutesValue', { count: prepTime ?? 0 }),
          cook: t('recipes.table.minutesValue', { count: cookTime ?? 0 }),
        })
      : null;

  function onInvalid() {
    setTab('details');
  }

  // The recipe is saved first; its staged pictures upload afterwards. A failed
  // picture never undoes the save — the drawer still closes with one toast.
  async function createWithPictures(request: CreateRecipeRequest) {
    try {
      const created = await createMutation.mutateAsync(request);
      if (created.recipeId && (staged.main || staged.extras.length > 0)) {
        const recipeId = created.recipeId;
        setUploading(true);
        const failed = await uploadStagedPictures(
          staged,
          (file) => uploads.uploadMain(recipeId, file),
          (file) => uploads.uploadGallery(recipeId, file),
        );
        setUploading(false);
        if (failed > 0) {
          showPartialUploadError(failed);
        }
      }
      onClose();
    } catch {
      // The create hook already toasted the error; the drawer stays open for a retry.
    }
  }

  function onValid(values: FormValues) {
    const ingredientsValid = lines.length > 0 && lines.every((line) => isValidAmount(line.amountText));
    if (!ingredientsValid) {
      setShowIngredientErrors(true);
      setTab('ingredients');
      return;
    }

    const stepsValid = steps.every((step) => isValidStepTitle(step.title) && isValidMinutesText(step.minutesText));
    if (!stepsValid) {
      setShowStepErrors(true);
      setTab('preparation');
      return;
    }

    const content = {
      name: values.name.trim(),
      description: values.description.trim() || undefined,
      prepTimeMinutes: values.prepTimeMinutes,
      cookTimeMinutes: values.cookTimeMinutes,
      servings: values.servings,
      visibility: values.visibility as RecipeVisibility,
      difficulty: values.difficulty ? (values.difficulty as RecipeDifficulty) : undefined,
      mealTypes: values.mealTypes as RecipeMealType[],
      dietaryPreferences: values.dietaryPreferences as DietaryPreference[],
      steps: steps
        .map((step) => ({
          title: step.title.trim(),
          description: step.text.trim(),
          minutes: parseMinutes(step.minutesText),
        }))
        .filter((step) => step.title !== '' || step.description !== '')
        .map((step): RecipeStepDto => {
          const dto: RecipeStepDto = { description: step.description };
          if (step.title !== '') dto.title = step.title;
          if (step.minutes !== undefined) dto.minutes = step.minutes;
          return dto;
        }),
      foods: lines.map((line) => ({
        foodExternalId: line.foodId,
        amountGrams: parseAmount(line.amountText),
        note: line.note,
      })),
    };

    if (mode === 'create' || !recipe?.recipeId) {
      const request: CreateRecipeRequest = content;
      void createWithPictures(request);
      return;
    }

    // Full-state PUT: `note` is not editable here, so it round-trips from the
    // loaded recipe.
    const request: UpdateRecipeRequest = { ...content, version: recipe.version, note: recipe.note };
    updateMutation.mutate({ recipeId: recipe.recipeId, request }, { onSuccess: onClose });
  }

  return (
    <>
      <form onSubmit={handleSubmit(onValid, onInvalid)} className="flex min-h-0 flex-1 flex-col px-6">
        <Tabs value={tab} onValueChange={(value) => setTab(value as TabValue)} className="min-h-0 flex-1 gap-4">
          <TabsList variant="underline" aria-label={t('recipes.drawer.tabsLabel')}>
            <TabsTrigger variant="underline" value="details">
              {t('recipes.drawer.tabDetails')}
            </TabsTrigger>
            <TabsTrigger variant="underline" value="ingredients">
              {t('recipes.drawer.tabIngredients')}
              <Badge variant="library">{lines.length}</Badge>
            </TabsTrigger>
            <TabsTrigger variant="underline" value="preparation">
              {t('recipes.drawer.tabPreparation')}
              <Badge variant="library">{steps.length}</Badge>
            </TabsTrigger>
            <TabsTrigger variant="underline" value="pictures">
              {t('recipes.drawer.tabPictures')}
            </TabsTrigger>
          </TabsList>

          <TabsContent value="details" className="flex flex-col gap-6 overflow-y-auto pb-4">
            <fieldset disabled={readOnly} className="flex flex-col gap-6">
              <div className="grid grid-cols-2 gap-4">
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="recipe-name">
                    {t('recipes.drawer.name')} <span className="text-destructive">*</span>
                  </Label>
                  <Input
                    id="recipe-name"
                    {...register('name')}
                    aria-invalid={!!errors.name}
                    placeholder={t('recipes.drawer.namePlaceholder')}
                    className="h-10"
                  />
                  {errors.name && <p className="text-meta text-destructive">{t('recipes.drawer.nameRequired')}</p>}
                </div>
                <div className="flex flex-col gap-1.5">
                  <Label htmlFor="recipe-servings">
                    {t('recipes.drawer.servings')} <span className="text-destructive">*</span>
                  </Label>
                  <Input
                    id="recipe-servings"
                    type="number"
                    {...register('servings', { valueAsNumber: true })}
                    aria-invalid={!!errors.servings}
                    className="h-10"
                  />
                  {errors.servings && (
                    <p className="text-meta text-destructive">{t('recipes.drawer.servingsRequired')}</p>
                  )}
                </div>
              </div>

              <div className="flex flex-col gap-1.5">
                <Label htmlFor="recipe-description">{t('recipes.drawer.description')}</Label>
                <Textarea
                  id="recipe-description"
                  rows={3}
                  {...register('description')}
                  placeholder={t('recipes.drawer.descriptionPlaceholder')}
                />
              </div>

              {!readOnly && (
                <VisibilityToggle
                  id="recipe-visibility"
                  entity="recipe"
                  value={visibility}
                  onChange={(next) => setValue('visibility', next, { shouldDirty: true })}
                />
              )}

              <div className="flex flex-col gap-3">
                <h3 className="text-body font-semibold text-foreground uppercase">{t('recipes.drawer.recipeDetails')}</h3>
                <div className="grid grid-cols-2 gap-4">
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="recipe-meal-types">
                      {t('recipes.drawer.mealTypes')} <span className="text-destructive">*</span>
                    </Label>
                    <MultiSelectPopover
                      id="recipe-meal-types"
                      placeholder={t('recipes.drawer.mealTypesPlaceholder')}
                      options={MEAL_TYPE_VALUES.map((value) => ({ value, label: t(`recipes.mealType.${value}`) }))}
                      selected={mealTypes}
                      onChange={(values) => setValue('mealTypes', values, { shouldValidate: true, shouldTouch: true })}
                    />
                    {errors.mealTypes && (
                      <p className="text-meta text-destructive">{t('recipes.drawer.mealTypesRequired')}</p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="recipe-difficulty">{t('recipes.drawer.difficulty')}</Label>
                    <select id="recipe-difficulty" {...register('difficulty')} className={SELECT_CLASS}>
                      <option value="">{t('recipes.drawer.difficultyNone')}</option>
                      {DIFFICULTY_VALUES.map((value) => (
                        <option key={value} value={value}>
                          {t(`recipes.difficulty.${value}`)}
                        </option>
                      ))}
                    </select>
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="recipe-prep-time">{t('recipes.drawer.prepTime')}</Label>
                    <Input
                      id="recipe-prep-time"
                      type="number"
                      {...register('prepTimeMinutes', { setValueAs: optionalMinutes })}
                      aria-invalid={!!errors.prepTimeMinutes}
                      placeholder={t('recipes.drawer.timePlaceholder')}
                      className="h-10"
                    />
                    {errors.prepTimeMinutes && (
                      <p className="text-meta text-destructive">{t('recipes.drawer.minutesInvalid')}</p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="recipe-cook-time">{t('recipes.drawer.cookTime')}</Label>
                    <Input
                      id="recipe-cook-time"
                      type="number"
                      {...register('cookTimeMinutes', { setValueAs: optionalMinutes })}
                      aria-invalid={!!errors.cookTimeMinutes}
                      placeholder={t('recipes.drawer.timePlaceholder')}
                      className="h-10"
                    />
                    {errors.cookTimeMinutes && (
                      <p className="text-meta text-destructive">{t('recipes.drawer.minutesInvalid')}</p>
                    )}
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <span className="text-meta font-medium text-ink-2">{t('recipes.drawer.allergens')}</span>
                    <div
                      className="flex min-h-10 flex-wrap items-center gap-1 rounded-md border border-input bg-muted px-3 py-1"
                      data-testid="recipe-allergens"
                    >
                      {allergens.length === 0 ? (
                        <span className="text-body text-faint">{t('recipes.drawer.allergensNone')}</span>
                      ) : (
                        allergens.map((allergen) => (
                          <Badge key={allergen} variant="library">
                            {t(`ingredients.allergen.${allergen}`)}
                          </Badge>
                        ))
                      )}
                    </div>
                    <p className="text-meta text-muted-foreground">{t('recipes.drawer.allergensHint')}</p>
                  </div>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="recipe-dietary-preferences">{t('recipes.drawer.dietaryPreferences')}</Label>
                    <MultiSelectPopover
                      id="recipe-dietary-preferences"
                      placeholder={t('recipes.drawer.dietaryPreferencesPlaceholder')}
                      options={DIETARY_PREFERENCE_VALUES.map((value) => ({
                        value,
                        label: t(`ingredients.dietaryPreference.${value}`),
                      }))}
                      selected={dietaryPreferences}
                      onChange={(values) => setValue('dietaryPreferences', values)}
                    />
                  </div>
                </div>
              </div>
            </fieldset>

            {/* Coach-private tags — deliberately OUTSIDE the fieldset above:
                a nutritionist can tag ANY visible recipe (system, shared, own),
                even one whose fields are read-only. Hidden on create — tag
                assignment needs a saved recipe id. */}
            {recipe?.recipeId && (
              <div className="flex flex-col gap-3 border-t border-border pt-6">
                <h3 className="flex items-center gap-2 text-body font-semibold text-foreground">
                  <Tag className="size-4 text-muted-foreground" aria-hidden="true" />
                  {t('recipes.drawer.myTags')}
                </h3>
                <div className="flex flex-wrap items-center gap-2">
                  {assignedTags.map((tag) => (
                    <TagPill key={tag.tagId} name={tag.name ?? ''} colorHex={tag.colorHex} />
                  ))}
                  <LibraryTagPickerPopover
                    assignedTags={assignedTags}
                    isReplacing={replaceTagsMutation.isPending}
                    onReplace={replaceTags}
                  />
                </div>
              </div>
            )}
          </TabsContent>

          <TabsContent value="ingredients" className="flex flex-col overflow-y-auto pb-4">
            <RecipeIngredientsTab
              lines={lines}
              onLinesChange={setLines}
              readOnly={readOnly}
              servings={Number.isFinite(servings) ? servings : 1}
              showErrors={showIngredientErrors}
            />
          </TabsContent>

          <TabsContent value="preparation" className="flex flex-col overflow-y-auto pb-4">
            <RecipePreparationTab
              steps={steps}
              onStepsChange={setSteps}
              readOnly={readOnly}
              timesSummary={timesSummary}
              showErrors={showStepErrors}
            />
          </TabsContent>

          <TabsContent value="pictures" className="flex flex-col overflow-y-auto pb-4">
            {recipe?.recipeId ? (
              <RecipePicturesTab
                recipeId={recipe.recipeId}
                imageUrl={recipe.imageUrl ?? undefined}
                galleryImageUrls={recipe.galleryImageUrls ?? []}
                readOnly={readOnly}
              />
            ) : (
              <StagedPicturesField
                staged={staged}
                disabled={isUploadingPictures}
                alt={t('recipes.picture.alt')}
                testIdPrefix="recipe-gallery"
              />
            )}
          </TabsContent>
        </Tabs>
      </form>

      <SheetFooter className="flex-row justify-between p-6">
        {mode === 'edit' ? (
          <Button type="button" variant="destructive" onClick={onDeleteClick}>
            {t('recipes.drawer.delete')}
          </Button>
        ) : (
          <span />
        )}
        <div className="flex gap-2">
          <Button type="button" variant="outline" disabled={isUploadingPictures} onClick={onClose}>
            {mode === 'view' ? t('common.close') : t('common.cancel')}
          </Button>
          {mode !== 'view' && (
            <Button type="button" disabled={isPending} onClick={handleSubmit(onValid, onInvalid)}>
              {isUploadingPictures && <Loader2 className="animate-spin" aria-hidden="true" />}
              {t('recipes.drawer.save')}
            </Button>
          )}
        </div>
      </SheetFooter>
    </>
  );
}

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** The list row to edit or view. `null` opens the drawer in create mode. */
  recipe: RecipeSummaryDto | null;
  /** True for a system/shared recipe, or when the caller may not edit. */
  readOnly: boolean;
}

/**
 * Create / edit / read-only drawer for a recipe, in four tabs (Details,
 * Ingredients, Preparation, Pictures). In create mode the Pictures tab stages
 * files in the browser and uploads them once the recipe is saved. The list row
 * only carries a summary, so an existing recipe's full detail is fetched when
 * the drawer opens.
 */
export default function RecipeDrawer({ open, onOpenChange, recipe, readOnly }: Props) {
  const { t } = useTranslation();
  const mode = recipe === null ? 'create' : readOnly ? 'view' : 'edit';
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [busy, setBusy] = useState(false);
  const detailQuery = useRecipe(open ? recipe?.recipeId : undefined);
  const detail = detailQuery.data ?? null;

  function close() {
    onOpenChange(false);
  }

  const title =
    mode === 'create'
      ? t('recipes.drawer.createTitle')
      : mode === 'view'
        ? t('recipes.drawer.viewTitle')
        : t('recipes.drawer.editTitle');
  const subtitle =
    mode === 'create'
      ? t('recipes.drawer.subtitleCreate')
      : mode === 'view'
        ? t('recipes.drawer.subtitleView')
        : t('recipes.drawer.subtitleEdit');

  return (
    <>
      {/* Esc, overlay and the close button all land here — refuse them while pictures upload. */}
      <Sheet open={open} onOpenChange={(next) => !(busy && !next) && onOpenChange(next)}>
        <SheetContent className="sm:w-drawer-wide sm:max-w-none">
          <SheetHeader className="gap-1 border-b border-border p-6">
            <SheetTitle className="text-panel-title font-bold">{title}</SheetTitle>
            <SheetDescription className="text-body">{subtitle}</SheetDescription>
          </SheetHeader>

          {mode === 'create' && (
            <RecipeForm recipe={null} mode={mode} onClose={close} onDeleteClick={() => undefined} onBusyChange={setBusy} />
          )}

          {mode !== 'create' && detailQuery.isPending && (
            <div className="flex flex-1 flex-col gap-3 p-6">
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-10 w-full" />
              <Skeleton className="h-24 w-full" />
            </div>
          )}

          {mode !== 'create' && detailQuery.isError && (
            <div className="flex flex-1 flex-col items-center justify-center gap-3 p-6 text-center">
              <p className="text-body text-muted-foreground">{t('recipes.drawer.loadError')}</p>
              <Button type="button" variant="outline" size="sm" onClick={() => void detailQuery.refetch()}>
                {t('recipes.retry')}
              </Button>
            </div>
          )}

          {mode !== 'create' && detail && (
            <RecipeForm
              key={detail.recipeId}
              recipe={detail}
              mode={mode}
              onClose={close}
              onDeleteClick={() => setDeleteOpen(true)}
              onBusyChange={setBusy}
            />
          )}
        </SheetContent>
      </Sheet>
      {recipe?.recipeId && (
        <DeleteRecipeDialog
          open={deleteOpen}
          onOpenChange={setDeleteOpen}
          recipeId={recipe.recipeId}
          recipeName={recipe.name ?? ''}
          onDeleted={() => {
            setDeleteOpen(false);
            close();
          }}
        />
      )}
    </>
  );
}
