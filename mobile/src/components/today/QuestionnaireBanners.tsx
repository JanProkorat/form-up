import { usePhotoRequestLists } from '@/components/photoRequest/hooks';
import { bannerRequests } from '@/components/photoRequest/model';
import { usePendingQuestionnaires } from '@/hooks/usePendingQuestionnaires';
import { usePhotos } from '@/hooks/usePhotos';

import { fillableForms } from './pendingForms';
import { PhotoRequestBanner } from './PhotoRequestBanner';
import { QuestionnaireBanner } from './QuestionnaireBanner';

/** A banner per open photo request, then per questionnaire, waiting on the client; nothing while loading or on failure. */
export function QuestionnaireBanners() {
  const { data } = usePendingQuestionnaires();
  const { data: lists } = usePhotoRequestLists();
  const { data: months } = usePhotos();
  return (
    <>
      {bannerRequests(data?.diaryRequests, lists, months).map((entry) => (
        <PhotoRequestBanner
          key={entry.kind === 'pending' ? entry.item.requestPublicId : entry.request.id}
          entry={entry}
        />
      ))}
      {fillableForms(data?.questionnaires).map((item) => (
        <QuestionnaireBanner
          key={`${item.linkPublicId}:${item.responsePublicId ?? item.questionnairePublicId ?? ''}`}
          item={item}
        />
      ))}
    </>
  );
}
